using System.Text.Json;
using AcsTool.Core.Cwmp;
using AcsTool.Simulator.Engine;
using AcsTool.Simulator.Profiles;
using AcsTool.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcsTool.Simulator.Tests;

public class ProfileTests
{
    [Fact]
    public void DefaultProfiles_Validate()
    {
        Assert.Empty(ProfileValidator.Validate(DefaultProfiles.Tr098()));
        Assert.Empty(ProfileValidator.Validate(DefaultProfiles.Tr181()));
    }

    [Theory]
    [InlineData(DataModelRoot.InternetGatewayDevice)]
    [InlineData(DataModelRoot.Device)]
    public void DefaultSeed_ContainsEveryForcedParameter_MarkedForInform(DataModelRoot root)
    {
        var p = root == DataModelRoot.Device ? DefaultProfiles.Tr181() : DefaultProfiles.Tr098();
        foreach (var forced in DefaultProfiles.Forced(root))
        {
            var def = Assert.Single(p.Parameters, x => x.Name == forced);
            Assert.True(def.IncludeInInform, forced);
        }
        Assert.Equal(p.DeviceId.SerialNumber, p.Parameters.Single(x => x.Name.EndsWith("DeviceInfo.SerialNumber", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public async Task JsonProfileStore_SaveLoad_RoundTrips()
    {
        using var paths = new TempAppPaths();
        var store = new JsonProfileStore(paths, NullLogger<JsonProfileStore>.Instance);
        var p = DefaultProfiles.Tr181() with
        {
            Username = "cpe",
            PasswordProtected = "plain:pw",
            RpcBehaviors = new Dictionary<string, RpcBehavior> { ["Upload"] = new(RpcBehaviorKind.Fault, 9001) },
            Version = CwmpVersion.V1_2,
        };
        await store.SaveAsync(p, default);
        await store.SaveAsync(DefaultProfiles.Tr098(), default);
        var all = await store.LoadAllAsync(default);
        Assert.Equal(2, all.Count);
        var loaded = all.Single(x => x.Id == p.Id);
        Assert.Equal(JsonSerializer.Serialize(p, SimulatorJsonContext.Default.CpeProfile), JsonSerializer.Serialize(loaded, SimulatorJsonContext.Default.CpeProfile));
        Assert.Contains("\"oui\": \"00D09E\"", File.ReadAllText(store.PathFor(p.Id)), StringComparison.Ordinal);
        await store.DeleteAsync(p.Id, default);
        Assert.Single(await store.LoadAllAsync(default));
    }

    [Fact]
    public async Task JsonProfileStore_CorruptFile_Skipped()
    {
        using var paths = new TempAppPaths();
        Directory.CreateDirectory(paths.ProfilesDir);
        await File.WriteAllTextAsync(Path.Combine(paths.ProfilesDir, "bad.json"), "{ nope");
        var store = new JsonProfileStore(paths, NullLogger<JsonProfileStore>.Instance);
        Assert.Empty(await store.LoadAllAsync(default));
    }

    public static TheoryData<string, string> BadProfiles() => new()
    {
        { "oui", "DeviceId.OUI" },
        { "serial", "DeviceId.SerialNumber" },
        { "dup", "Duplicate parameter name" },
        { "typed", "is not a valid xsd:unsignedInt" },
        { "url", "AcsUrl" },
        { "count", "Instances.Count" },
        { "root", "not under the profile root" },
    };

    [Theory]
    [MemberData(nameof(BadProfiles))]
    public void Validator_ReportsErrors(string kind, string expected)
    {
        var p = DefaultProfiles.Tr098();
        p = kind switch
        {
            "oui" => p with { DeviceId = p.DeviceId with { OUI = "00d09" } },
            "serial" => p with { DeviceId = p.DeviceId with { SerialNumber = " " } },
            "dup" => p with { Parameters = [.. p.Parameters, p.Parameters[0]] },
            "typed" => p with { Parameters = [.. p.Parameters, new ParameterDefinition("InternetGatewayDevice.X.Count", XsdTypes.UnsignedInt, "-1")] },
            "url" => p with { AcsUrl = "ftp://acs" },
            "count" => p with { Instances = new MultiInstanceSettings(Count: 3) },
            "root" => p with { Parameters = [.. p.Parameters, new ParameterDefinition("Device.X", XsdTypes.String, "")] },
            _ => p,
        };
        var errors = ProfileValidator.Validate(p);
        Assert.Contains(errors, e => e.ToString().Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Expand_SerialPattern()
    {
        var p = DefaultProfiles.Tr098(serial: "ABC") with { Instances = new MultiInstanceSettings(3, "SIM{i:D6}-{serial}", 7) };
        Assert.Equal(new[] { "SIM000007-ABC", "SIM000008-ABC", "SIM000009-ABC" }, SimulationEngine.Expand(p).Select(d => d.SerialNumber));
        var dup = p with { Instances = new MultiInstanceSettings(2, "X") };
        Assert.Throws<ProfileValidationException>(() => SimulationEngine.Expand(dup));
    }
}
