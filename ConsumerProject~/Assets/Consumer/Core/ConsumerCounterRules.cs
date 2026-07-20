namespace ConsumerProject.Core
{
    /// <summary>
    /// Applies the consumer project's one deterministic counter mutation.
    /// </summary>
    public sealed class ConsumerCounterRules
    {
        public const int InitialValue = 10;
        public const int RequiredIncrement = 5;
        public const int FinalValue = InitialValue + RequiredIncrement;

        /// <summary>
        /// Validate and apply the supported increment without changing caller-owned state.
        /// </summary>
        public ConsumerCounterIncrementResult TryIncrement(int currentValue, int increment)
        {
            if (currentValue != InitialValue)
            {
                return ConsumerCounterIncrementResult.Rejected(
                    $"Expected current counter {InitialValue}, received {currentValue}.");
            }

            if (increment != RequiredIncrement)
            {
                return ConsumerCounterIncrementResult.Rejected(
                    $"Expected increment {RequiredIncrement}, received {increment}.");
            }

            return ConsumerCounterIncrementResult.Applied(FinalValue);
        }
    }

    /// <summary>
    /// Carries the explicit result of applying the project counter rule.
    /// </summary>
    public readonly struct ConsumerCounterIncrementResult
    {
        private ConsumerCounterIncrementResult(
            bool succeeded,
            int updatedValue,
            string failure)
        {
            Succeeded = succeeded;
            UpdatedValue = updatedValue;
            Failure = failure;
        }

        public bool Succeeded { get; }
        public int UpdatedValue { get; }
        public string Failure { get; }

        public static ConsumerCounterIncrementResult Applied(int updatedValue)
        {
            return new ConsumerCounterIncrementResult(true, updatedValue, null);
        }

        public static ConsumerCounterIncrementResult Rejected(string failure)
        {
            return new ConsumerCounterIncrementResult(false, 0, failure);
        }
    }
}
