using SubathonManager.Core.Enums;

namespace SubathonManager.Tests.CoreUnitTests;

public class EnumValidationTests
{
    [Fact]
    public void AssertEnumGroups()
    {
        var streamSources = Enum.GetValues<SubathonEventSource>().Where(x => 
            x.GetGroup() == SubathonSourceGroup.Stream).ToList();
        Assert.True(streamSources.Any());
        Assert.Contains(SubathonEventSource.Twitch, streamSources);
        Assert.Contains(SubathonEventSource.YouTube, streamSources);
        Assert.DoesNotContain(SubathonEventSource.GoAffPro, streamSources);
        
        var externalServices = Enum.GetValues<SubathonEventSource>().Where(x => 
            x.GetGroup() == SubathonSourceGroup.ExternalService).ToList();
        Assert.True(externalServices.Any());
        Assert.Contains(SubathonEventSource.KoFi, externalServices);
        Assert.Contains(SubathonEventSource.GoAffPro, externalServices);
        Assert.DoesNotContain(SubathonEventSource.Twitch, externalServices);
        
        var extensionServices = Enum.GetValues<SubathonEventSource>().Where(x => 
            x.GetGroup() == SubathonSourceGroup.StreamExtension).ToList();
        Assert.True(extensionServices.Any());
        Assert.Contains(SubathonEventSource.Blerp, extensionServices);
        Assert.Contains(SubathonEventSource.StreamElements, extensionServices);
        Assert.DoesNotContain(SubathonEventSource.Picarto, extensionServices);
        
        var eventTypes = Enum.GetValues<SubathonEventType>().GroupBy(x => x.GetSource()).ToList();
        Assert.True(eventTypes.Any());
        foreach (var group in eventTypes)
        {
            if (group.Key == SubathonEventSource.GoAffPro)
            {
                foreach (var subathonEventType in group)
                {
                    var eventType = (SubathonEventType?) subathonEventType;
                    Assert.StartsWith(subathonEventType.GetTypeTrueSource() ?? "ERROR_ERROR_ERROR", subathonEventType.ToString());
                    Assert.True(Enum.TryParse(subathonEventType.GetTypeTrueSource(), true, out GoAffProSource gapSource));
                    Assert.True(gapSource.GetSiteId() > 0);
                    Assert.True(gapSource.GetOrderEvent() == subathonEventType);
                    Assert.True(eventType.IsOrder());
                    Assert.EndsWith("Order", subathonEventType.ToString());
                    Assert.Equal(eventType.GetDescription(), eventType.GetLabel());
                }
            }

            if (group.Key.GetGroup() == SubathonSourceGroup.Stream ||
                group.Key.GetGroup() == SubathonSourceGroup.ExternalService ||
                group.Key.GetGroup() == SubathonSourceGroup.StreamExtension)
            {
                if (group.Key == SubathonEventSource.GoAffPro) continue;
                if (group.Key == SubathonEventSource.Twitch)
                {
                    Assert.True(group.Key.IsEnabled());
                    Assert.False(group.Key.IsDisabled());
                }
                foreach (var subathonEventType in group)
                {
                    if (subathonEventType == SubathonEventType.TwitchSub)
                    {
                        Assert.True(group.Key.IsEnabled());
                        Assert.False(group.Key.IsDisabled());
                    }
                    Assert.True(subathonEventType.GetOrderNumber() > 0);
                    
                    var eventType = (SubathonEventType?) subathonEventType;
                    Assert.Contains(group.Key.ToString(), subathonEventType.ToString());
                    if (eventType.IsSubscription() && !eventType.IsGift())
                    {
                        Assert.Contains(eventType.GetLabel(), "Subscription Membership");
                    }

                    if (eventType.IsGift())
                    {
                        Assert.Contains("Gift", eventType.GetLabel());
                        Assert.Contains(eventType.GetLabel(), "Gift Subscription Gift Membership");
                    }

                    if (eventType.IsOrder())
                    {
                        Assert.Contains("Order", subathonEventType.ToString());
                    }

                    if (eventType.IsCurrencyDonation())
                    {
                        Assert.Contains(eventType.GetLabel(), "Charity Donation Tip SuperChat");
                    }

                    Assert.Equal(eventType.GetDescription(), $"{eventType.GetSource()} {eventType.GetLabel()}");
                }
                
                Assert.Contains(group.Key.GetLabel(), $"{group.Key.GetGroup().GetLabel()} ||||| {group.Key}");
            }
        }
    }

}