namespace Pakt;

sealed partial class Parser
{
    internal enum StepStatus
    {
        Continue,
        Event,
        MoreData,
        Complete,
        Error,
    }
}
