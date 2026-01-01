using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PennedObjects.RateLimiting
{
    /// <summary>
    ///     http://www.pennedobjects.com/2010/10/better-rate-limiting-with-dot-net/
    ///     Used to control the rate of some occurrence per unit of time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         To control the rate of an action using a <see cref="RateGate" />,
    ///         code should simply call <see cref="WaitToProceed()" /> prior to
    ///         performing the action. <see cref="WaitToProceed()" /> will block
    ///         the current thread until the action is allowed based on the rate
    ///         limit.
    ///     </para>
    ///     <para>
    ///         This class is thread safe. A single <see cref="RateGate" /> instance
    ///         may be used to control the rate of an occurrence across multiple
    ///         threads.
    ///     </para>
    /// </remarks>
    public class RateGate : IDisposable
    {
        /// <summary>
        /// Timer used to trigger exiting the semaphore.
        /// </summary>
        private Timer _exitTimer;
        private readonly ConcurrentQueue<int> _exitTimes;
        /// <summary>
        /// Semaphore used to count and limit the number of occurrences per
        /// </summary>
        private SemaphoreSlim _semaphore;

        /// <summary>
        ///  Whether this instance is disposed.
        /// </summary>
        private int _isDisposed;

        /// <summary>
        /// Indicates whether a timer callback is currently executing.
        /// </summary>
        private int _timerCallbackRunning;

        /// <summary>
        /// Lock object for timer operations
        /// </summary>
        private readonly object _timerLock = new object();

        /// <summary>
        ///     Initializes a new instance of the <see cref="RateGate"/> class with a specified rate of occurrences
        ///     per time unit.
        /// </summary>
        /// <param name="occurrences">The number of occurrences allowed per unit of time.</param>
        /// <param name="timeUnit">The length of the time unit.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        ///     Thrown when <paramref name="occurrences"/> is less than or equal to zero, or when <paramref name="timeUnit"/>
        ///     is not a positive span of time, or when <paramref name="timeUnit"/> is greater than or equal to 2^32 milliseconds.
        /// </exception>
        public RateGate(int occurrences, TimeSpan timeUnit)
        {
            // Check the arguments.
            if (occurrences <= 0)
                throw new ArgumentOutOfRangeException(nameof(occurrences), "Number of occurrences must be a positive integer");
            if (timeUnit <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be a positive span of time");
            if (timeUnit >= TimeSpan.FromMilliseconds(uint.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be less than 2^32 milliseconds");

            Occurrences = occurrences;
            TimeUnitMilliseconds = (int)timeUnit.TotalMilliseconds;

            // Create the semaphore, with the number of occurrences as the maximum count.
            _semaphore = new SemaphoreSlim(Occurrences, Occurrences);

            // Create a queue to hold the semaphore exit times.
            _exitTimes = new ConcurrentQueue<int>();

            // Create a timer to exit the semaphore. Use the time unit as the original
            // interval length because that's the earliest we will need to exit the semaphore.
            _exitTimer = new Timer(ExitTimerCallback, null, TimeUnitMilliseconds, -1);
        }

        /// <summary>
        ///     Number of occurrences allowed per unit of time.
        /// </summary>
        public int Occurrences { get; }

        /// <summary>
        ///     The length of the time unit, in milliseconds.
        /// </summary>
        public int TimeUnitMilliseconds { get; }

        /// <summary>
        ///     Releases unmanaged resources held by an instance of this class.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Callback for the exit timer that exits the semaphore based on exit times
        /// in the queue and then sets the timer for the next exit time.
        /// </summary>
        /// <param name="state">An object containing information to be used by the callback method.</param>
        private void ExitTimerCallback(object state)
        {
            // Use interlocked to ensure only one timer callback runs at a time
            // Also check if RateGate has been disposed
            if (Interlocked.CompareExchange(ref _timerCallbackRunning, 1, 0) != 0 ||
                Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
            {
                return;
            }

            try
            {
                int currentTickCount = Environment.TickCount;
                int releasedCount = 0;

                // While there are exit times that are passed due still in the queue,
                // count them and dequeue them
                int exitTime;
                while (_exitTimes.TryPeek(out exitTime)
                       && unchecked(exitTime - currentTickCount) <= 0)
                {
                    _exitTimes.TryDequeue(out _);
                    releasedCount++;
                }

                // Release the semaphore in a single batch operation
                if (releasedCount > 0)
                {
                    SemaphoreSlim semaphore = _semaphore;
                    if (semaphore != null)
                    {
                        try
                        {
                            semaphore.Release(releasedCount);
                        }
                        catch (ObjectDisposedException)
                        {
                            // Semaphore was disposed, exit gracefully
                            return;
                        }
                        catch (SemaphoreFullException)
                        {
                            // This shouldn't happen in normal operation, but handle it gracefully
                            return;
                        }
                    }
                }

                // Try to get the next exit time from the queue and compute
                // the time until the next check should take place. If the
                // queue is empty, then no exit times will occur until at least
                // one time unit has passed.
                int timeUntilNextCheck;
                if (_exitTimes.TryPeek(out exitTime))
                    timeUntilNextCheck = unchecked(exitTime - Environment.TickCount);
                else
                    timeUntilNextCheck = TimeUnitMilliseconds;

                // Schedule the next timer callback with proper synchronization
                ScheduleNextTimerCallback(timeUntilNextCheck);
            }
            finally
            {
                // Allow the next callback to run
                Interlocked.Exchange(ref _timerCallbackRunning, 0);
            }
        }

        /// <summary>
        /// Safely schedules the next timer callback
        /// </summary>
        /// <param name="delayInMilliseconds">Delay in milliseconds</param>
        private void ScheduleNextTimerCallback(int delayInMilliseconds)
        {
            lock (_timerLock)
            {
                // Check disposal state
                if (Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
                    return;

                // Capture the timer reference locally to avoid race with disposal
                Timer timer = _exitTimer;

                // Only proceed if we have a valid timer
                if (timer != null)
                {
                    try
                    {
                        timer.Change(delayInMilliseconds, -1);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Timer was disposed, exit gracefully
                    }
                }
            }
        }

        #region Synchronous methods

        /// <summary>
        ///     Blocks the current thread until allowed to proceed or until the
        ///     specified timeout elapses.
        /// </summary>
        /// <param name="millisecondsTimeout">Number of milliseconds to wait, or -1 to wait indefinitely.</param>
        /// <returns>true if the thread is allowed to proceed, or false if timed out</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="millisecondsTimeout"/> is less than -1.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the object is already disposed.</exception>
        public bool WaitToProceed(int millisecondsTimeout)
        {
            // Check the arguments.
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            CheckDisposed();

            // Capture semaphore reference to avoid race with disposal
            SemaphoreSlim semaphore = _semaphore;
            if (semaphore == null)
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");

            bool entered;
            try
            {
                // Block until we can enter the semaphore or until the timeout expires.
                entered = semaphore.Wait(millisecondsTimeout);
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");
            }

            // If we entered the semaphore, compute the corresponding exit time
            // and add it to the queue.
            if (entered)
            {
                int timeToExit = unchecked(Environment.TickCount + TimeUnitMilliseconds);
                _exitTimes.Enqueue(timeToExit);
            }

            return entered;
        }

        /// <summary>
        ///     Blocks the current thread until allowed to proceed or until the
        ///     specified timeout elapses.
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns>true if the thread is allowed to proceed, or false if timed out</returns>
        public bool WaitToProceed(TimeSpan timeout)
        {
            long num = (long)timeout.TotalMilliseconds;
            if (num < -1 || num > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");
            }
            return WaitToProceed((int)timeout.TotalMilliseconds);
        }

        /// <summary>
        ///     Blocks the current thread indefinitely until allowed to proceed.
        /// </summary>
        public void WaitToProceed()
        {
            _ = WaitToProceed(Timeout.Infinite);
        }

        #endregion Synchronous methods

        #region Asynchronous methods

        public async Task<bool> WaitToProceedAsync(int millisecondsTimeout, CancellationToken cancellationToken = default)
        {
            CheckDisposed();
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            // Capture semaphore reference to avoid race with disposal
            SemaphoreSlim semaphore = _semaphore;
            if (semaphore == null)
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");

            bool entered;
            try
            {
                entered = await semaphore.WaitAsync(millisecondsTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");
            }

            if (entered)
            {
                int timeToExit = unchecked(Environment.TickCount + TimeUnitMilliseconds);
                _exitTimes.Enqueue(timeToExit);
            }
            return entered;
        }

        public async Task<bool> WaitToProceedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            long num = (long)timeout.TotalMilliseconds;
            if (num < -1 || num > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");

            return await WaitToProceedAsync((int)timeout.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            await WaitToProceedAsync(Timeout.Infinite, cancellationToken)
                .ConfigureAwait(false);
        }

        #endregion Asynchronous methods

        /// <summary>
        /// Throws an <see cref="ObjectDisposedException"/> if this object is disposed.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown when the object is already disposed.</exception>
        private void CheckDisposed()
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");
        }

        /// <summary>
        ///     Releases unmanaged resources held by an instance of this class.
        /// </summary>
        /// <param name="isDisposing">Whether this object is being disposed.</param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0 && isDisposing)
            {
                // Safely dispose of the timer first to prevent new callbacks
                ClearTimer();

                // Dispose and null out the semaphore using interlocked exchange
                SemaphoreSlim semaphore = Interlocked.Exchange(ref _semaphore, null);
                semaphore?.Dispose();
            }
        }

        /// <summary>
        /// Safely clears and disposes of the exit timer if it exists.
        /// </summary>
        private void ClearTimer()
        {
            lock (_timerLock)
            {
                Timer timer = Interlocked.Exchange(ref _exitTimer, null);
                if (timer != null)
                {
                    try
                    {
                        timer.Change(Timeout.Infinite, Timeout.Infinite);
                        timer.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Timer already disposed, ignore
                    }
                }
            }
        }
    }
}