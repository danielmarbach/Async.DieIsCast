using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NUnit.Framework;

namespace AsyncDolls
{
    [TestFixture]
    public class AsyncScript
    {
        [Test]
        public async Task CPUBound()
        {
            Parallel.For(0, 1000, CpuBoundMethod);
            Parallel.ForEach(Enumerable.Range(1000, 2000), CpuBoundMethod);

            await Task.Run(() => CpuBoundMethod(2001));
            await Task.Factory.StartNew(() => CpuBoundMethod(2002));
        }

        static void CpuBoundMethod(int i)
        {
            Console.WriteLine(i);
        }

        [Test]
        public async Task IOBound()
        {
            await IoBoundMethod();
        }

        static async Task IoBoundMethod()
        {
            using (var stream = new FileStream(".\\IoBoundMethod.txt", FileMode.OpenOrCreate))
            using (var writer = new StreamWriter(stream))
            {
                await writer.WriteLineAsync("42");
                writer.Close();
                stream.Close();
            }
        }

        [Test]
        public async Task Sequential()
        {
            var sequential = Enumerable.Range(0, 4).Select(t => Task.Delay(1500));

            foreach (var task in sequential)
            {
                await task;
            }
        }

        [Test]
        public async Task Concurrent()
        {
            var concurrent = Enumerable.Range(0, 4).Select(t => Task.Delay(1500));
            await Task.WhenAll(concurrent);
        }

        [Test]
        public async Task AsyncVoid()
        {
            try
            {
                AvoidAsyncVoid();

            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine(e);
            }
            await Task.Delay(100);
        }

        static async void AvoidAsyncVoid()
        {
            Console.WriteLine("Going inside async void.");
            await Task.Delay(10);
            Console.WriteLine("Going to throw soon");
            throw new InvalidOperationException("Gotcha!");
        }

        [Test]
        public async Task ConfigureAwait()
        {
            // ReSharper disable once PossibleNullReferenceException
            await Process.Start(new ProcessStartInfo(@".\configureawait.exe") { UseShellExecute = false });
        }

        [Test]
        public void DontMixBlockingAndAsync()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());

            Delay(15);
        }


        static void Delay(int milliseconds)
        {
            DelayAsync(milliseconds).Wait();
        }

        static async Task DelayAsync(int milliseconds)
        {
            await Task.Delay(milliseconds);
        }

        [Test]
        public async Task ShortcutTheStatemachine()
        {
            await DoesNotShortcut();

            await DoesShortcut();
        }

        private static async Task DoesNotShortcut()
        {
            await Task.Delay(1);
        }

        private static Task DoesShortcut()
        {
            return Task.Delay(1);
        }

        /*
private static Task DoesNotShortcut()
{
  AsyncScript.\u003CDoesNotShortcut\u003Ed__12 stateMachine;
  stateMachine.\u003C\u003Et__builder = AsyncTaskMethodBuilder.Create();
  stateMachine.\u003C\u003E1__state = -1;
  stateMachine.\u003C\u003Et__builder.Start<AsyncScript.\u003CDoesNotShortcut\u003Ed__12>(ref stateMachine);
  return stateMachine.\u003C\u003Et__builder.Task;
}
private static Task DoesShortcut()
{
  return Task.Delay(1);
}

*/

        [Test]
        public async Task TheCompletePumpWithBlockingHandleMessage()
        {
            #region Concurrency Limit and Task Tracking

            var runningTasks = new ConcurrentDictionary<Task, Task>();
            var semaphore = new SemaphoreSlim(100);
            var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var token = tokenSource.Token;
            int numberOfTasks = 0;

            #endregion

            var pumpTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    #region Concurrency Limit

                    await semaphore.WaitAsync(token).ConfigureAwait(false);

                    #endregion

                    var runningTask = Task.Run(() =>
                    {
                        Interlocked.Increment(ref numberOfTasks);

                        return BlockingHandleMessage(token);
                    }, CancellationToken.None);

                    #region Task Tracking

                    runningTasks.TryAdd(runningTask, runningTask);

                    runningTask.ContinueWith(t =>
                    {
                        semaphore.Release();

                        Task taskToBeRemoved;
                        runningTasks.TryRemove(t, out taskToBeRemoved);
                    }, TaskContinuationOptions.ExecuteSynchronously)
                        .Ignore();

                    #endregion
                }
            });

            #region Await Completion

            await pumpTask.IgnoreCancellation().ConfigureAwait(false);
            await Task.WhenAll(runningTasks.Values).IgnoreCancellation().ConfigureAwait(false);
            tokenSource.Dispose();

            $"Consumed {numberOfTasks} messages with concurrency {semaphore.CurrentCount} in 5 seconds. Throughput {numberOfTasks/5} msgs/s"
                .Output();

            #endregion
        }

        private static Task BlockingHandleMessage(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            Thread.Sleep(1000);
            return Task.CompletedTask;
        }

        [Test]
        public async Task TheCompletePumpWithAsyncHandleMessage()
        {
            #region Concurrency Limit and Task Tracking
            var runningTasks = new ConcurrentDictionary<Task, Task>();
            var semaphore = new SemaphoreSlim(100);
            var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var token = tokenSource.Token;
            int numberOfTasks = 0;
            #endregion

            var pumpTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    #region Concurrency Limit

                    await semaphore.WaitAsync(token).ConfigureAwait(false);

                    #endregion

                    var task = HandleMessageWithCancellation(token);

                    #region Task Tracking
                    Interlocked.Increment(ref numberOfTasks);

                    runningTasks.TryAdd(task, task);

                    task.ContinueWith(t =>
                    {
                        semaphore.Release();
                        Task taskToBeRemoved;
                        runningTasks.TryRemove(t, out taskToBeRemoved);
                    }, TaskContinuationOptions.ExecuteSynchronously)
                        .Ignore();

                    #endregion
                }
            });

            #region Await completion

            await pumpTask.IgnoreCancellation().ConfigureAwait(false);
            await Task.WhenAll(runningTasks.Values).IgnoreCancellation().ConfigureAwait(false);
            tokenSource.Dispose();
            semaphore.Dispose();

            $"Consumed {numberOfTasks} messages with concurrency {semaphore.CurrentCount} in 5 seconds. Throughput {numberOfTasks / 5} msgs/s"
                .Output();

            #endregion
        }

        static Task HandleMessageWithCancellation(CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Delay(1000, cancellationToken);
        }
    }
}