using System.Reflection;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
namespace SubathonManager.Tests.Utility;

public class EventUtil
{
    public static class SubathonEventCapture
    {
        private static readonly Lock Lock = new();

        public static SubathonEvent? Capture(Action trigger)
        {
            lock (Lock)
            {
                typeof(SubathonEvents)
                    .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.SetValue(null, null);

                SubathonEvent? captured = null;
                void Handler(SubathonEvent e) => captured = e;

                SubathonEvents.SubathonEventCreated += Handler;
                try
                {
                    trigger();
                    return captured;
                }
                finally
                {
                    SubathonEvents.SubathonEventCreated -= Handler;
                }
            }
        }

        public static SubathonEvent? CaptureRequired(Action trigger)
        {
            var ev = Capture(trigger);
            return ev;
        }
    }
}
