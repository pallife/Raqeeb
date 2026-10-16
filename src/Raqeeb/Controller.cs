namespace Raqeeb;

/// <summary>يدير دورة حياة الوكيل: يقرأ الإعدادات، ينشئ الخدمة والاتصال، ويعيد التحميل عند تغيير .env.</summary>
internal sealed class Controller
{
    public static readonly UnlockHelperClient Helper = new();
    public readonly List<string> Log = new();
    public event Action? Changed;

    public Config Config { get; private set; }
    public AgentOptions Options { get; private set; } = new();
    public string[] Problems { get; private set; } = Array.Empty<string>();

    private AgentClient? _client; private AgentService? _svc; private OutboxWatcher? _outbox;
    private readonly Action<string, string> _balloon;
    private readonly string? _envOverride;

    public Controller(Action<string, string> balloon, string? envOverride = null)
    {
        _balloon = balloon; _envOverride = envOverride;
        Config = Config.Load(envOverride);
    }

    public bool Running => _client is not null;
    public bool Connected => _client?.Connected == true;
    public string Status => Problems.Length > 0 ? "الإعداد ناقص: " + Problems[0] : _client?.Status ?? "متوقف";

    private void AddLog(string s)
    {
        lock (Log) { Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {s}"); if (Log.Count > 200) Log.RemoveAt(Log.Count - 1); }
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        var c = _client; _client = null;
        _outbox?.Dispose(); _outbox = null;
        _svc?.Shutdown(); _svc = null;
        if (c is not null) await c.StopAsync();
        Changed?.Invoke();
    }

    /// <summary>يعيد قراءة .env ويشغّل الوكيل من جديد.</summary>
    public async Task ReloadAsync()
    {
        await StopAsync();
        Config = Config.Load(_envOverride);
        Options = Config.ToOptions();
        Problems = Config.Problems().ToArray();
        if (Problems.Length > 0) { AddLog("⚠ " + string.Join(" | ", Problems)); return; }
        if (!Options.Enabled) { AddLog("معطّل (RAQEEB_ENABLED=false)"); return; }
        if (Options.PinHash.Length == 0) AddLog("⚠ لم يُضبط RAQEEB_PIN — الإجراءات مرفوضة والقراءة فقط مسموحة.");
        Start();
    }

    private void Start()
    {
        var relay = Config.Str("RAQEEB_RELAY_URL").TrimEnd('/');
        var token = Config.WebToken();
        var key = Config.AgentKey()!;
        var o = Options;
        _svc = new AgentService(key, o, new AppHost(_balloon), new WindowsAgentSystem(), null, Helper)
        {
            Desktop = new WindowsAgentDesktop(),
            TelegramFile = (path, caption, ct) => RelayNotifier.SendFileAsync(relay, token, path, caption, null, ct),
            Notifier = text => RelayNotifier.SendAsync(relay, token, text),
            ClipboardSeq = Hooks.ClipboardSeq,
            ClipboardToTelegram = ct => Hooks.ClipboardToTelegram(relay, token, ct),
            PrivacyScreen = Hooks.Privacy,
            CapsChanged = () =>
            {
                // تغييرات الجوال (فأرة/ملفات/صندوق) تُحفظ في .env لتبقى بعد إعادة التشغيل
                Config.Set("RAQEEB_ALLOW_INPUT", o.AllowInput ? "true" : "false");
                Config.Set("RAQEEB_ALLOW_FILES", o.AllowFiles ? "true" : "false");
                Config.Set("RAQEEB_OUTBOX", o.OutboxEnabled ? "true" : "false");
                SetOutbox(o.OutboxEnabled, relay, token);
                Changed?.Invoke();
            },
            Audit = AddLog,
        };
        SetOutbox(o.OutboxEnabled, relay, token);
        _client = new AgentClient(relay, () => token, o, _svc);
        _client.Changed += () => Changed?.Invoke();
        _client.Start();
        AddLog($"بدأ رقيب — الجهاز «{o.DeviceName}» ({o.DeviceId}) ← {relay}");
        Changed?.Invoke();
    }

    private void SetOutbox(bool on, string relay, string token)
    {
        _outbox?.Dispose(); _outbox = null;
        if (!on) return;
        _outbox = new OutboxWatcher(OutboxWatcher.DefaultFolder(), (p, c, ct) => RelayNotifier.SendFileAsync(relay, token, p, c, null, ct)) { Log = AddLog };
        _outbox.Start();
    }
}
