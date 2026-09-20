using IniParser.Model;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Tests.Utility;

public class MockConfig {
    public static IConfig MakeMockConfig(Dictionary<(string, string), string>? values = null) {
        var mock = new Mock<IConfig>();
        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out string? v) ? v : d);
        mock.Setup(c => c.GetOrderTypeMode(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OrderTypeModes>()))
            .Returns((string s, string k, OrderTypeModes d) =>
                values != null && values.TryGetValue((s, $"{k}.Mode"), out string? v)
                    ? Enum.Parse<OrderTypeModes>(v)
                    : d);
        mock.Setup(c => c.GetBool(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string s, string k, bool d) =>
                values != null && values.TryGetValue((s, k), out string? v)
                    ? bool.TryParse(v, out bool boolParse) ? boolParse : d
                    : d);
        mock.Setup(c => c.GetFromEncoded(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out string? v) ? v : d);

        if (values == null) return mock.Object;
        foreach ((string, string) valueTuple in values.Keys) {
            (string section, string key) = valueTuple;
            mock.Setup(c => c.GetSection(section)).Returns(() => {
                var kdc = new KeyDataCollection();
                string val = values.TryGetValue(valueTuple, out string? v) ? v : "";
                kdc.AddKey(new KeyData(key) {
                    Value = val
                });
                return kdc;
            });
        }

        /** Forced KeyData **/

        var kd = new KeyData("Commands.Pause");
        kd.Value = "pause";
        mock.Setup(c => c.GetSection("Chat")).Returns(() => {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd);
            return kdc;
        });

        return mock.Object;
    }
}