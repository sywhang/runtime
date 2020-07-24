using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;
using Xunit;

namespace BasicEventSourceTests
{
    class TestEventSource : EventSource
    {
        public class Keywords
        {
            public const EventKeywords One = (EventKeywords)1;
            public const EventKeywords Two = (EventKeywords)2;
            public const EventKeywords Four = (EventKeywords)4;
            public const EventKeywords Eight = (EventKeywords)8;
        }
        private static EventSourceOptions InformationalOption = new EventSourceOptions() { Level=EventLevel.Informational };

        private static EventSourceOptions VerboseOption = new EventSourceOptions() { Level=EventLevel.Verbose };
        private static EventSourceOptions KeywordOneOption = new EventSourceOptions() { Keywords=Keywords.One };

        private static EventSourceOptions MyOption2 = new EventSourceOptions() { Level=EventLevel.Verbose, Keywords=EventKeywords.All };

        [Event(1, Level=EventLevel.Verbose, Keywords=EventKeywords.None)]
        public void VerboseEvent() { Write("VerboseEvent"); }

        [Event(2, Level=EventLevel.Verbose, Keywords=EventKeywords.None)]
        public void VerboseEventWithInformationalOption() { Write("VerboseEventWithInformationalOption", InformationalOption); }

        [Event(3, Level=EventLevel.Informational, Keywords=EventKeywords.None)]
        public void InformationalEventWithOneKeyword() { Write("VerboseEventWithInformationalOption", KeywordOneOption); }

        
    }
    class SimpleEventListener : EventListener
    {
        private Dictionary<string, int> m_observedCounts;
        private int m_ExpectedEventCount;
        private string m_ProviderToCheck;

        private EventLevel m_EventLevel;
        private EventKeywords m_EventKeywords;

        public SimpleEventListener(string providerToCheck, int expectedCount, EventLevel level, EventKeywords keywords)
        {
            m_observedCounts = new Dictionary<string, int>();
            m_ProviderToCheck = providerToCheck;
            m_ExpectedEventCount = expectedCount;
            m_EventLevel = level;
            m_EventKeywords = keywords;
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name.Equals(m_ProviderToCheck))
            {
                EnableEvents(source, m_EventLevel, m_EventKeywords);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (m_observedCounts.ContainsKey(args.EventName))
            {
                m_observedCounts[args.EventName] += 1;
            }
            else
            {
                m_observedCounts[args.EventName] = 1;
            }
        }

        public void Validate(string eventName, int expectedCount)
        {
            Assert.True(m_observedCounts.ContainsKey(eventName));
            if (m_observedCounts.ContainsKey(eventName))
            {
                if (expectedCount == -1)
                {
                    Assert.True(m_observedCounts[eventName] > 0);
                }
                else
                {
                    Assert.Equal(expectedCount, m_observedCounts[eventName]);
                }
            }
        }

        public void ValidateDoesNotExist(string eventName)
        {
            Assert.False(m_observedCounts.ContainsKey(eventName));
        }
    }

    public class TestsEventListenerFiltering
    {
        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/21569", TargetFrameworkMonikers.NetFramework)]
        public void Test_EventListener_Filter_LowLevel()
        {
            // Keyword matches, but EventLevel does not.
            SimpleEventListener listener = new SimpleEventListener("TestEventSource", 1, EventLevel.Informational, EventKeywords.None);
            using (TestEventSource log = new TestEventSource())
            {
                log.VerboseEvent();
                listener.ValidateDoesNotExist("VerboseEvent");
            }
        }

        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/21569", TargetFrameworkMonikers.NetFramework)]
        public void Test_EventListener_Filter_SameEvent_ManyListener()
        {
            // Gave one listener at informational level, one at verbose level. 
            // Write verbose and check only the verbose listener received the event.
            SimpleEventListener informationalListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Informational, EventKeywords.None);
            SimpleEventListener verboseListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Verbose, EventKeywords.None);

            using (TestEventSource log = new TestEventSource())
            {
                log.VerboseEvent();
                informationalListener.ValidateDoesNotExist("VerboseEvent");
                verboseListener.Validate("VerboseEvent", -1);
            }
        }

        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/21569", TargetFrameworkMonikers.NetFramework)]
        public void Test_EventListener_Filter_SameEvent_ManyListener_WithOptions()
        {
            // Gave one listener at informational level, one at verbose level. 
            // Write a verbose event, but with a informational option and verify both listeners receive it.
            SimpleEventListener informationalListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Informational, EventKeywords.None);
            SimpleEventListener verboseListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Verbose, EventKeywords.None);

            using (TestEventSource log = new TestEventSource())
            {
                log.VerboseEventWithInformationalOption();
                informationalListener.Validate("VerboseEventWithInformationalOption", -1);
                verboseListener.Validate("VerboseEventWithInformationalOption", -1);
            }
        }

        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/21569", TargetFrameworkMonikers.NetFramework)]
        public void Test_EventListener_Filter_SameEvent_ManyListener_WithOptions_2()
        {
            // Gave one listener at informational level, one at verbose level, both without any keywords.
            // Write a informational event, but with a keyword option and verify both listeners don't receive it.
            SimpleEventListener informationalListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Informational, EventKeywords.None);
            SimpleEventListener verboseListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Verbose, EventKeywords.None);

            using (TestEventSource log = new TestEventSource())
            {
                log.InformationalEventWithOneKeyword();
                informationalListener.ValidateDoesNotExist("InformationalEventWithOneKeyword");
                verboseListener.ValidateDoesNotExist("InformationalEventWithOneKeyword");
            }
        }

        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/21569", TargetFrameworkMonikers.NetFramework)]
        public void Test_EventListener_Filter_SameEvent_ManyListener_WithOptions_3()
        {
            // Gave one listener at informational level, one at verbose level, both without any keywords.
            // Write a informational event, but with a keyword option and verify both listeners don't receive it.
            SimpleEventListener informationalListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Informational, EventKeywords.None);
            SimpleEventListener verboseListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Verbose, EventKeywords.None);

            using (TestEventSource log = new TestEventSource())
            {
                log.InformationalEventWithOneKeyword();
                informationalListener.ValidateDoesNotExist("InformationalEventWithOneKeyword");
                verboseListener.ValidateDoesNotExist("InformationalEventWithOneKeyword");
            }
        }

        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/21569", TargetFrameworkMonikers.NetFramework)]
        public void Test_EventListener_Filter_LowLevel3()
        {
            SimpleEventListener testEventSourceListener = new SimpleEventListener("TestEventSource", 1, EventLevel.Informational, EventKeywords.None);
            SimpleEventListener counterListener = new SimpleEventListener("System.Runtime", 0, EventLevel.Informational, EventKeywords.None); // just meant to trigger counters
            using (TestEventSource log = new TestEventSource())
            {
                log.VerboseEvent();
                Thread.Sleep(2000); // Sleep for a couple seconds to aggregate counter values.

                counterListener.Validate("EventCounter", -1);
                counterListener.ValidateDoesNotExist("EventCounter");
                testEventSourceListener.Validate("VerboseEvent", 1);
                testEventSourceListener.ValidateDoesNotExist("EventCounter");
            }
        }
    }
}
