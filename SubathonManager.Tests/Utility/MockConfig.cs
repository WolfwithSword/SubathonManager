using IniParser.Model;
using Moq;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Tests.Utility;

public class MockConfig
{
    public static IConfig MakeMockConfig(Dictionary<(string, string), string>? values = null)
    {
        var mock = new Mock<IConfig>();
        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out var v) ? v : d);
        mock.Setup(c => c.GetBool(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string s, string k, bool d) =>
                values != null && values.TryGetValue((s, k), out var v) ? bool.TryParse(v, out var boolParse) ? boolParse : d : d);
        mock.Setup(c => c.GetFromEncoded(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out var v) ? v : d);

        if (values == null) return mock.Object;
        foreach (var valueTuple in values.Keys)
        {
            var (section, key) = valueTuple;
            mock.Setup(c => c.GetSection(section)).Returns(() =>
            {
                var kdc = new KeyDataCollection();
                var val = values.TryGetValue(valueTuple, out var v) ? v : "";
                kdc.AddKey(new KeyData(key)
                {
                    Value = val
                });
                return kdc;
            });
        }
            
        return mock.Object;
    }
}