using System.Collections.ObjectModel;
using System.Globalization;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Settings;
using AcsTool.Simulator.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcsTool.Presentation.Simulator;

public sealed partial class EventChoiceViewModel(string code, bool isSelected, string commandKey = "") : ObservableObject
{
    public string Code { get; } = code;

    public bool IsMethodEvent => EventCodes.IsMethodEvent(Code);

    [ObservableProperty]
    private bool _isSelected = isSelected;

    [ObservableProperty]
    private string _commandKey = commandKey;
}

public sealed partial class ParameterRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _type;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private bool _writable;

    [ObservableProperty]
    private bool _includeInInform;

    [ObservableProperty]
    private int _notification;

    public ParameterRowViewModel(ParameterDefinition d, bool isForced)
    {
        ArgumentNullException.ThrowIfNull(d);
        _name = d.Name;
        _type = XsdTypes.Normalize(d.XsdType);
        _value = d.Value;
        _writable = d.Writable;
        _includeInInform = d.IncludeInInform || isForced;
        _notification = d.Notification;
        IsForced = isForced;
    }

    /// <summary>Forced Inform parameters (Appendix B.4): the Inform flag is locked on.</summary>
    public bool IsForced { get; }

    public static IReadOnlyList<string> Types => XsdTypes.All;

    public ParameterDefinition ToDefinition() => new(Name.Trim(), Type, Value ?? "", Writable, IncludeInInform || IsForced, Notification);

    partial void OnIncludeInInformChanged(bool value)
    {
        if (IsForced && !value)
        {
            IncludeInInform = true;
        }
    }
}

/// <summary>Profile editor (plan §4.4, MVP subset): device, ACS, credentials, events, parameters.</summary>
public sealed partial class CpeProfileEditorViewModel : ObservableObject
{
    private readonly ISecretProtector _secrets;
    private CpeProfile _original;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _manufacturer = "";
    [ObservableProperty] private string _oui = "";
    [ObservableProperty] private string _productClass = "";
    [ObservableProperty] private string _serialNumber = "";
    [ObservableProperty] private string _acsUrl = "";
    [ObservableProperty] private string? _username;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private CwmpVersion _version;
    [ObservableProperty] private int _requestTimeoutSeconds;
    [ObservableProperty] private string _customEvent = "";
    [ObservableProperty] private bool _isDirty;

    public CpeProfileEditorViewModel(CpeProfile profile, ISecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _secrets = secrets;
        _original = profile;
        Load(profile);
    }

    public Guid Id => _original.Id;

    public CpeProfile Original => _original;

    public string DisplayName => (IsDirty ? "● " : "") + Name;

    public ObservableCollection<EventChoiceViewModel> Events { get; } = [];

    public ObservableCollection<ParameterRowViewModel> Parameters { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public static IReadOnlyList<CwmpVersion> Versions { get; } = [CwmpVersion.V1_0, CwmpVersion.V1_1, CwmpVersion.V1_2, CwmpVersion.V1_3, CwmpVersion.V1_4];

    public bool HasErrors => Errors.Count > 0;

    private void Load(CpeProfile p)
    {
        Name = p.Name;
        Manufacturer = p.DeviceId.Manufacturer;
        Oui = p.DeviceId.OUI;
        ProductClass = p.DeviceId.ProductClass;
        SerialNumber = p.DeviceId.SerialNumber;
        AcsUrl = p.AcsUrl;
        Username = p.Username;
        Password = string.IsNullOrEmpty(p.PasswordProtected) ? null : _secrets.Unprotect(p.PasswordProtected);
        Version = p.Version;
        RequestTimeoutSeconds = p.RequestTimeoutSeconds;
        Events.Clear();
        foreach (var code in EventCodes.Standard.Concat(EventCodes.MethodEvents))
        {
            var chosen = p.DefaultEvents.FirstOrDefault(e => e.EventCode == code);
            Events.Add(Track(new EventChoiceViewModel(code, chosen is not null, chosen?.CommandKey ?? "")));
        }
        foreach (var custom in p.DefaultEvents.Where(e => !Events.Any(x => x.Code == e.EventCode)))
        {
            Events.Add(Track(new EventChoiceViewModel(custom.EventCode, true, custom.CommandKey)));
        }
        Parameters.Clear();
        var forced = DefaultProfiles.Forced(p.Root).ToHashSet(StringComparer.Ordinal);
        foreach (var d in p.Parameters)
        {
            Parameters.Add(Track(new ParameterRowViewModel(d, forced.Contains(d.Name))));
        }
        Errors.Clear();
        IsDirty = false;
    }

    private T Track<T>(T vm) where T : ObservableObject
    {
        vm.PropertyChanged += (_, _) => IsDirty = true;
        return vm;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(IsDirty) or nameof(DisplayName) or nameof(HasErrors)))
        {
            IsDirty = true;
        }
        if (e.PropertyName is nameof(IsDirty) or nameof(Name))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    /// <summary>Builds the profile from the editor fields (OUI upper-cased; password protected via ISecretProtector).</summary>
    public CpeProfile ToProfile() => _original with
    {
        Name = Name.Trim(),
        DeviceId = new DeviceIdStruct(Manufacturer.Trim(), Oui.Trim().ToUpperInvariant(), ProductClass.Trim(), SerialNumber.Trim()),
        AcsUrl = AcsUrl.Trim(),
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        PasswordProtected = string.IsNullOrEmpty(Password) ? null : _secrets.Protect(Password),
        Version = Version,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        DefaultEvents = Events.Where(e => e.IsSelected).Select(e => new EventStruct(e.Code, e.IsMethodEvent ? e.CommandKey : "")).ToList(),
        Parameters = Parameters.Select(r => r.ToDefinition()).ToList(),
    };

    /// <summary>Validates; fills <see cref="Errors"/>. Returns the profile when valid.</summary>
    public CpeProfile? Validate()
    {
        var p = ToProfile();
        var errors = ProfileValidator.Validate(p);
        Errors.Clear();
        foreach (var e in errors)
        {
            Errors.Add(e.ToString());
        }
        OnPropertyChanged(nameof(HasErrors));
        return errors.Count == 0 ? p : null;
    }

    /// <summary>Called after a successful save.</summary>
    public void AcceptSaved(CpeProfile saved)
    {
        _original = saved;
        IsDirty = false;
    }

    [RelayCommand]
    private void AddParameter() =>
        Parameters.Add(Track(new ParameterRowViewModel(new ParameterDefinition(_original.RootPrefix + "X_New.Parameter" +
            (Parameters.Count + 1).ToString(CultureInfo.InvariantCulture), XsdTypes.String, ""), false)));

    [RelayCommand]
    private void RemoveParameter(ParameterRowViewModel? row)
    {
        if (row is not null && !row.IsForced)
        {
            Parameters.Remove(row);
            IsDirty = true;
        }
    }

    [RelayCommand]
    private void ResetParameters()
    {
        var forced = DefaultProfiles.Forced(_original.Root).ToHashSet(StringComparer.Ordinal);
        Parameters.Clear();
        foreach (var d in DefaultProfiles.Seed(_original.Root, ToProfile().DeviceId))
        {
            Parameters.Add(Track(new ParameterRowViewModel(d, forced.Contains(d.Name))));
        }
        IsDirty = true;
    }

    [RelayCommand]
    private void AddCustomEvent()
    {
        var code = CustomEvent.Trim();
        if (code.Length > 0 && !Events.Any(e => e.Code == code))
        {
            Events.Add(Track(new EventChoiceViewModel(code, true)));
            CustomEvent = "";
        }
    }

    public IReadOnlyList<EventStruct> SelectedEvents() =>
        Events.Where(e => e.IsSelected).Select(e => new EventStruct(e.Code, e.IsMethodEvent ? e.CommandKey : "")).ToList();
}
