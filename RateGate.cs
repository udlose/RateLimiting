﻿using System;
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
            // While there are exit times that are passed due still in the queue,
            // exit the semaphore and dequeue the exit time.
            int exitTime;
            while (_exitTimes.TryPeek(out exitTime)
                   && unchecked(exitTime - Environment.TickCount) <= 0)
            {
                _semaphore.Release();
                _exitTimes.TryDequeue(out _);
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

            // Set the timer.
            _exitTimer.Change(timeUntilNextCheck, -1);
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

            // Block until we can enter the semaphore or until the timeout expires.
            bool entered = _semaphore.Wait(millisecondsTimeout);

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

            bool entered = await _semaphore.WaitAsync(millisecondsTimeout, cancellationToken)
                .ConfigureAwait(false);

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
                // Cancel the timer first to prevent new callbacks
                _exitTimer.Change(Timeout.Infinite, Timeout.Infinite);

                // The semaphore and timer both implement IDisposable and 
                // therefore must be disposed.
                // Explicitly set the members to null to cause NullReferenceException
                // if someone tries to use this object after it's disposed.
                _semaphore.Dispose();
                _semaphore = null;
                _exitTimer.Dispose();
                _exitTimer = null;
            }
        }
    }
}