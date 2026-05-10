namespace Pakt;

sealed partial class Parser
{
    internal readonly ref struct StepResult
    {
        public readonly StepStatus Status;
        public readonly PaktEvent PaktEvent;
        public readonly PaktParseError? ParseError;

        private StepResult(StepStatus status, PaktEvent evt, PaktParseError? error)
        {
            Status = status;
            PaktEvent = evt;
            ParseError = error;
        }

        public static StepResult Event(PaktEvent evt)
            => new(StepStatus.Event, evt, error: null);

        public static StepResult Continue()
            => new(StepStatus.Continue, evt: default, error: null);

        public static StepResult MoreData()
            => new(StepStatus.MoreData, evt: default, error: null);

        public static StepResult Complete()
            => new(StepStatus.Complete, evt: default, error: null);

        public static StepResult Error(PaktParseError error)
            => new(StepStatus.Error, evt: default, error);
    }
}
