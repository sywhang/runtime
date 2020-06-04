
namespace System.Diagnostics.Tracing
{
    //
    // This is an internal EventSource that logs the list of counters enabled
    // in a given application when the user initiates a counter session.
    //
    internal sealed class CounterEventSource : EventSource
    {
        private static CounterEventSource? s_RuntimeEventSource;

        [NonEvent]
        public unsafe void LogEventCounterInfo(string providerName, string counterName)
        {
            fixed(char * providerPtr = providerName)
            {
                fixed (char * counterPtr = counterName)
                {
                    EventData* data = stackalloc EventData[2];
                    
                    data[0].DataPointer = providerPtr;
                    data[0].Size = (providerName.Length + 1) * 2;
                    data[1].DataPointer =  counterPtr;
                    data[1].Size = (counterName.Length + 1) * 2;

                    WriteEventCore(1, 2, data);
                }
            }
        }
    }
}
