using System;
using System.Collections.Generic;

namespace PennedObjects.RateLimiting
{
    public static class EnumerableExtensions
    {
        /// <summary>
        /// Limits the rate at which the sequence is enumerated.
        /// </summary>
        /// <typeparam name="T">The type of the elements of <paramref name="source"/>.</typeparam>
        /// <param name="source">The <see cref="IEnumerable{T}"/> whose enumeration is to be rate limited.</param>
        /// <param name="count">The number of items in the sequence that are allowed to be processed per time unit.</param>
        /// <param name="timeUnit">Length of the time unit.</param>
        /// <returns>An <see cref="IEnumerable{T}"/> containing the elements of the source sequence.</returns>
        public static IEnumerable<T> LimitRate<T>(this IEnumerable<T> source, int count, TimeSpan timeUnit)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");
            if (timeUnit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be greater than zero.");

            return LimitRateIterator();

            IEnumerable<T> LimitRateIterator()
            {
                using (RateGate rateGate = new RateGate(count, timeUnit))
                {
                    foreach (T item in source)
                    {
                        rateGate.WaitToProceed();
                        yield return item;
                    }
                }
            }
        }
    }
}