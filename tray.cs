using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_QUERYENDSESSION = 0x0011;
    internal const uint WM_ENDSESSION = 0x0016;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
}

internal sealed class TrayConfig
{
    public string Node;
    public string Script;
    public List<string> NodeArgs;

    public TrayConfig(string node, string script, List<string> nodeArgs)
    {
        Node = node;
        Script = script;
        NodeArgs = nodeArgs ?? new List<string>();
    }

    public string BuildArguments(IList<string> extra)
    {
        List<string> all = new List<string>();
        foreach (string a in NodeArgs) all.Add(QuoteArg(a));
        all.Add(QuoteArg(Script));
        foreach (string a in extra) all.Add(QuoteArg(a));
        return string.Join(" ", all.ToArray());
    }

    public static string QuoteArg(string value)
    {
        if (value == null) return "\"\"";
        if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0) return value;
        StringBuilder sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0) sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    public static TrayConfig Discover(string explicitNode, string explicitScript)
    {
        if (!string.IsNullOrEmpty(explicitNode) && !string.IsNullOrEmpty(explicitScript))
        {
            return new TrayConfig(explicitNode, explicitScript, new List<string>());
        }
        JavaScriptSerializer serializer = JsonUtil.CreateSerializer();
        List<string> candidates = new List<string>();
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(exeDir))
        {
            candidates.Add(Path.Combine(exeDir, "menubar.json"));
            DirectoryInfo parent = Directory.GetParent(exeDir.TrimEnd('\\', '/'));
            if (parent != null) candidates.Add(Path.Combine(parent.FullName, "menubar.json"));
        }
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            candidates.Add(Path.Combine(appData, "Claude", "claude-transplant", "menubar.json"));
        }
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "claude-transplant", "menubar.json"));
            string packages = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packages))
            {
                try
                {
                    foreach (string pkg in Directory.GetDirectories(packages, "Claude_*"))
                    {
                        candidates.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude-transplant", "menubar.json"));
                    }
                }
                catch { }
            }
        }
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                Dictionary<string, object> data = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(candidate, Encoding.UTF8));
                string node = JsonUtil.Str(data, "node");
                string script = JsonUtil.Str(data, "script");
                List<string> nodeArgs = JsonUtil.StrList(data, "nodeArgs");
                if (!string.IsNullOrEmpty(node) && !string.IsNullOrEmpty(script))
                {
                    return new TrayConfig(node, script, nodeArgs);
                }
            }
            catch { }
        }
        if (!string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, "transplant.js")))
        {
            return new TrayConfig("node.exe", Path.Combine(exeDir, "transplant.js"), new List<string>());
        }
        return null;
    }
}

internal static class JsonUtil
{
    public static JavaScriptSerializer CreateSerializer()
    {
        JavaScriptSerializer s = new JavaScriptSerializer();
        s.MaxJsonLength = int.MaxValue;
        s.RecursionLimit = 256;
        return s;
    }

    public static string Str(IDictionary<string, object> dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return null;
        return Convert.ToString(dict[key], CultureInfo.InvariantCulture);
    }

    public static bool? Bool(IDictionary<string, object> dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return null;
        if (dict[key] is bool) return (bool)dict[key];
        bool parsed;
        if (bool.TryParse(Convert.ToString(dict[key], CultureInfo.InvariantCulture), out parsed)) return parsed;
        return null;
    }

    public static int? Int(IDictionary<string, object> dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return null;
        try { return Convert.ToInt32(dict[key], CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static double? Double(IDictionary<string, object> dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return null;
        try { return Convert.ToDouble(dict[key], CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static IDictionary<string, object> Obj(IDictionary<string, object> dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return null;
        return dict[key] as IDictionary<string, object>;
    }

    public static List<IDictionary<string, object>> ObjList(IDictionary<string, object> dict, string key)
    {
        List<IDictionary<string, object>> list = new List<IDictionary<string, object>>();
        if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return list;
        IEnumerable items = dict[key] as IEnumerable;
        if (items == null) return list;
        foreach (object item in items)
        {
            IDictionary<string, object> sub = item as IDictionary<string, object>;
            if (sub != null) list.Add(sub);
        }
        return list;
    }

    public static List<string> StrList(IDictionary<string, object> dict, string key)
    {
        List<string> list = new List<string>();
        if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return list;
        IEnumerable items = dict[key] as IEnumerable;
        if (items == null || dict[key] is string) return list;
        foreach (object item in items)
        {
            if (item != null) list.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
        }
        return list;
    }
}

internal sealed class FailureItem
{
    public string Id;
    public string SessionId;
    public string Label;
    public string Title;
    public string Error;
    public string Code;

    public static FailureItem FromJson(IDictionary<string, object> d)
    {
        if (d == null) return null;
        FailureItem f = new FailureItem();
        f.Id = JsonUtil.Str(d, "id");
        f.SessionId = JsonUtil.Str(d, "sessionId");
        f.Label = JsonUtil.Str(d, "label");
        f.Title = JsonUtil.Str(d, "title");
        f.Error = JsonUtil.Str(d, "error") ?? "";
        f.Code = JsonUtil.Str(d, "code");
        return f;
    }
}

internal sealed class FailureGroupItem
{
    public string Cause;
    public List<FailureItem> Records;

    public FailureGroupItem()
    {
        Records = new List<FailureItem>();
    }
}

internal sealed class ReceiptSummaryItem
{
    public int Moved;
    public List<FailureItem> NotMoved;
    public List<FailureGroupItem> Groups;
    public List<FailureItem> Attention;
    public List<FailureItem> Kept;

    public ReceiptSummaryItem()
    {
        NotMoved = new List<FailureItem>();
        Groups = new List<FailureGroupItem>();
        Attention = new List<FailureItem>();
        Kept = new List<FailureItem>();
    }

    public static ReceiptSummaryItem FromJson(IDictionary<string, object> d)
    {
        if (d == null) return null;
        ReceiptSummaryItem r = new ReceiptSummaryItem();
        r.Moved = JsonUtil.Int(d, "moved") ?? 0;
        foreach (IDictionary<string, object> item in JsonUtil.ObjList(d, "notMoved")) r.NotMoved.Add(FailureItem.FromJson(item));
        foreach (IDictionary<string, object> item in JsonUtil.ObjList(d, "attention")) r.Attention.Add(FailureItem.FromJson(item));
        foreach (IDictionary<string, object> item in JsonUtil.ObjList(d, "kept")) r.Kept.Add(FailureItem.FromJson(item));
        foreach (IDictionary<string, object> grp in JsonUtil.ObjList(d, "groups"))
        {
            FailureGroupItem g = new FailureGroupItem();
            g.Cause = JsonUtil.Str(grp, "cause") ?? "";
            foreach (IDictionary<string, object> rec in JsonUtil.ObjList(grp, "records")) g.Records.Add(FailureItem.FromJson(rec));
            r.Groups.Add(g);
        }
        return r;
    }
}

internal sealed class RecoveryProblemItem
{
    public string Receipt;
    public string Error;
    public string Code;

    public static RecoveryProblemItem FromJson(IDictionary<string, object> d)
    {
        if (d == null) return null;
        RecoveryProblemItem p = new RecoveryProblemItem();
        p.Receipt = JsonUtil.Str(d, "receipt") ?? "";
        p.Error = JsonUtil.Str(d, "error") ?? "";
        p.Code = JsonUtil.Str(d, "code");
        return p;
    }
}

internal sealed class RestartWorkerItem
{
    public int Pid;
    public string Started;
    public string RecordId;
    public string Title;
    public string Cwd;

    public static RestartWorkerItem FromJson(IDictionary<string, object> d)
    {
        RestartWorkerItem w = new RestartWorkerItem();
        w.Pid = JsonUtil.Int(d, "pid") ?? 0;
        w.Started = JsonUtil.Str(d, "started") ?? "";
        w.RecordId = JsonUtil.Str(d, "recordId");
        w.Title = JsonUtil.Str(d, "title");
        w.Cwd = JsonUtil.Str(d, "cwd");
        return w;
    }
}

internal sealed class HeldRecordItem
{
    public string Id;
    public string Title;

    public static HeldRecordItem FromJson(IDictionary<string, object> d)
    {
        HeldRecordItem h = new HeldRecordItem();
        h.Id = JsonUtil.Str(d, "id") ?? "";
        h.Title = JsonUtil.Str(d, "title") ?? "";
        return h;
    }
}

internal sealed class AccountItem
{
    public string Account;
    public string Org;
    public string Email;
    public string OrgName;
    public string Label;
    public string Stats;
    public bool Active;
    public bool SignedIn;
    public double? ActiveAt;
    public string IdentityState;
    public string Pending;
    public string PendingAction;
    public string PendingState;
    public int? PendingCount;
    public bool PendingAutomatic;
    public double? PendingRetryAt;
    public int? PendingOpenCount;
    public int? PendingIdleSeconds;
    public bool PendingPartial;
    public string PendingCancel;
    public bool Kept;
    public int? Sessions;
    public int? ReceiptMoved;
    public string ReceiptDestination;
    public string Receipt;
    public string ReceiptDone;
    public ReceiptSummaryItem ReceiptSummary;
    public RecoveryProblemItem RecoveryProblem;
    public List<FailureItem> PendingFailures;
    public List<HeldRecordItem> PendingWaiting;

    public AccountItem()
    {
        PendingFailures = new List<FailureItem>();
        PendingWaiting = new List<HeldRecordItem>();
    }

    public string Id
    {
        get { return Account + "/" + Org; }
    }

    public string Selector
    {
        get { return Account + " " + Org; }
    }

    public string Name
    {
        get
        {
            if (!string.IsNullOrEmpty(Email)) return Email;
            return Account != null && Account.Length > 8 ? Account.Substring(0, 8) : (Account ?? "");
        }
    }

    public string Plan
    {
        get
        {
            if (!string.IsNullOrEmpty(OrgName)) return OrgName;
            return Org != null && Org.Length > 8 ? Org.Substring(0, 8) : (Org ?? "");
        }
    }

    public string MetaLine
    {
        get
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(Stats))
            {
                string[] raw = Stats.Split('|');
                for (int i = 0; i < raw.Length; i++)
                {
                    string piece = raw[i].Trim();
                    if (piece.Length == 0 || piece == "active") continue;
                    if (i == 0 && Regex.IsMatch(piece, @"^\d[\d,]*$"))
                    {
                        parts.Add(piece + (piece == "1" ? " chat" : " chats"));
                    }
                    else
                    {
                        parts.Add(piece);
                    }
                }
            }
            else if (Sessions.HasValue)
            {
                int n = Sessions.Value;
                parts.Add(n.ToString("N0", CultureInfo.InvariantCulture) + (n == 1 ? " chat" : " chats"));
            }
            return string.Join(" · ", parts.ToArray());
        }
    }

    public static AccountItem FromJson(IDictionary<string, object> d)
    {
        AccountItem a = new AccountItem();
        a.Account = JsonUtil.Str(d, "account") ?? "";
        a.Org = JsonUtil.Str(d, "org") ?? "";
        a.Email = JsonUtil.Str(d, "email");
        a.OrgName = JsonUtil.Str(d, "orgName");
        a.Label = JsonUtil.Str(d, "label") ?? (a.Name + " · " + a.Plan);
        a.Stats = JsonUtil.Str(d, "stats");
        a.Active = JsonUtil.Bool(d, "active") ?? false;
        a.SignedIn = JsonUtil.Bool(d, "signedIn") ?? false;
        a.ActiveAt = JsonUtil.Double(d, "activeAt");
        a.IdentityState = JsonUtil.Str(d, "identityState");
        a.Pending = JsonUtil.Str(d, "pending");
        a.PendingAction = JsonUtil.Str(d, "pendingAction");
        a.PendingState = JsonUtil.Str(d, "pendingState");
        a.PendingCount = JsonUtil.Int(d, "pendingCount");
        a.PendingAutomatic = JsonUtil.Bool(d, "pendingAutomatic") ?? false;
        a.PendingRetryAt = JsonUtil.Double(d, "pendingRetryAt");
        a.PendingOpenCount = JsonUtil.Int(d, "pendingOpenCount");
        a.PendingIdleSeconds = JsonUtil.Int(d, "pendingIdleSeconds");
        a.PendingPartial = JsonUtil.Bool(d, "pendingPartial") ?? false;
        a.PendingCancel = JsonUtil.Str(d, "pendingCancel");
        a.Kept = JsonUtil.Bool(d, "kept") ?? false;
        a.Sessions = JsonUtil.Int(d, "sessions");
        a.ReceiptMoved = JsonUtil.Int(d, "receiptMoved");
        a.ReceiptDestination = JsonUtil.Str(d, "receiptDestination");
        a.Receipt = JsonUtil.Str(d, "receipt");
        a.ReceiptDone = JsonUtil.Str(d, "receiptDone");
        a.ReceiptSummary = ReceiptSummaryItem.FromJson(JsonUtil.Obj(d, "receiptSummary"));
        a.RecoveryProblem = RecoveryProblemItem.FromJson(JsonUtil.Obj(d, "recoveryProblem"));
        foreach (IDictionary<string, object> item in JsonUtil.ObjList(d, "pendingFailures")) a.PendingFailures.Add(FailureItem.FromJson(item));
        foreach (IDictionary<string, object> item in JsonUtil.ObjList(d, "pendingWaiting")) a.PendingWaiting.Add(HeldRecordItem.FromJson(item));
        return a;
    }
}

internal sealed class MoveProgressState
{
    public double StartedAt;
    public string Stage;
    public int Completed;
    public int Total;
    public int SessionTotal;
    public int SessionRemaining;
    public bool HasSessions;
    public string Activity;

    public MoveProgressState(double now)
    {
        StartedAt = now;
        Stage = "scan";
        Completed = 0;
        Total = 0;
        SessionTotal = 0;
        SessionRemaining = 0;
        HasSessions = false;
        Activity = "";
    }

    public void Update(string stage, int completed, int total, double now)
    {
        if (!string.IsNullOrEmpty(stage)) Stage = stage;
        Completed = completed;
        Total = total;
    }

    public void SetSessions(int total, int remaining)
    {
        SessionTotal = total;
        SessionRemaining = remaining;
        HasSessions = true;
    }

    public double Fraction
    {
        get
        {
            if (Stage == "scan")
            {
                return Total > 0 ? Math.Min(0.18, 0.18 * Completed / (double)Total) : 0.05;
            }
            if (HasSessions && SessionTotal > 0)
            {
                double done = Math.Max(0, SessionTotal - SessionRemaining);
                return Math.Min(1.0, 0.18 + 0.82 * (done / (double)SessionTotal));
            }
            if (Total > 0)
            {
                return Math.Min(1.0, 0.18 + 0.82 * (Completed / (double)Total));
            }
            return 0.15;
        }
    }

    public string BadgeText
    {
        get
        {
            int pct = (int)Math.Round(Fraction * 100.0);
            if (pct < 1) pct = 1;
            if (pct > 99 && (HasSessions ? SessionRemaining > 0 : Completed < Total)) pct = 99;
            return pct.ToString(CultureInfo.InvariantCulture) + "%";
        }
    }

    public string LabelText
    {
        get
        {
            if (Stage == "scan")
            {
                return Total > 0
                    ? string.Format(CultureInfo.InvariantCulture, "Scanning history ({0}/{1})", Completed, Total)
                    : "Scanning history...";
            }
            if (HasSessions && SessionTotal > 0)
            {
                int moved = Math.Max(0, SessionTotal - SessionRemaining);
                return string.Format(CultureInfo.InvariantCulture, "Moving sessions ({0}/{1})", moved, SessionTotal);
            }
            if (Total > 0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} ({1}/{2})", Stage, Completed, Total);
            }
            return string.IsNullOrEmpty(Activity) ? "Working..." : Activity;
        }
    }
}

internal sealed class LogLine
{
    public string Stage;
    public string Text;

    public LogLine(string stage, string text)
    {
        Stage = stage ?? "";
        Text = text ?? "";
    }
}

internal sealed class CompletionBanner
{
    public string Title;
    public string Detail;

    public CompletionBanner(string title, string detail)
    {
        Title = title;
        Detail = detail;
    }
}

internal sealed class RestartPlanData
{
    public string Token;
    public string Kind;
    public bool Resume;
    public int HeldCount;
    public int? RemainingCount;
    public int Moved;
    public bool UndoRecovery;
    public List<RestartWorkerItem> Affected;
    public List<HeldRecordItem> Held;
    public List<string> SourceLabels;

    public RestartPlanData()
    {
        Affected = new List<RestartWorkerItem>();
        Held = new List<HeldRecordItem>();
        SourceLabels = new List<string>();
    }

    public static RestartPlanData FromEvent(IDictionary<string, object> d)
    {
        RestartPlanData p = new RestartPlanData();
        p.Token = JsonUtil.Str(d, "token") ?? "";
        p.Kind = JsonUtil.Str(d, "kind") ?? "move";
        p.Resume = JsonUtil.Bool(d, "resume") ?? false;
        p.HeldCount = JsonUtil.Int(d, "heldCount") ?? 0;
        p.RemainingCount = JsonUtil.Int(d, "remainingCount");
        p.Moved = JsonUtil.Int(d, "moved") ?? 0;
        p.UndoRecovery = JsonUtil.Bool(d, "undoRecovery") ?? false;
        p.SourceLabels = JsonUtil.StrList(d, "sourceLabels");
        foreach (IDictionary<string, object> w in JsonUtil.ObjList(d, "affected")) p.Affected.Add(RestartWorkerItem.FromJson(w));
        foreach (IDictionary<string, object> h in JsonUtil.ObjList(d, "held")) p.Held.Add(HeldRecordItem.FromJson(h));
        return p;
    }
}

internal sealed class TrayPreferences
{
    public bool AutoApproveRestart;

    public static string PrefsPath(TrayConfig config)
    {
        if (config != null && !string.IsNullOrEmpty(config.Script))
        {
            string dir = Path.GetDirectoryName(config.Script);
            if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "menubar-prefs.json");
        }
        return Path.Combine(Path.GetTempPath(), "claude-transplant-prefs.json");
    }

    public static TrayPreferences Load(TrayConfig config)
    {
        TrayPreferences prefs = new TrayPreferences();
        try
        {
            string path = PrefsPath(config);
            if (File.Exists(path))
            {
                IDictionary<string, object> d = JsonUtil.CreateSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                prefs.AutoApproveRestart = JsonUtil.Bool(d, "autoApproveRestart") ?? false;
            }
        }
        catch { }
        return prefs;
    }

    public void Save(TrayConfig config)
    {
        try
        {
            string path = PrefsPath(config);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["autoApproveRestart"] = AutoApproveRestart;
            File.WriteAllText(path, JsonUtil.CreateSerializer().Serialize(d), Encoding.UTF8);
        }
        catch { }
    }
}

internal sealed class TrayModel
{
    public TrayConfig Config;
    public TrayPreferences Prefs;
    public List<AccountItem> Accounts;
    public HashSet<string> Sources;
    public string Target;
    public bool Running;
    public string Note;
    public string Symbol;
    public string Badge;
    public MoveProgressState MoveProgress;
    public CompletionBanner Completion;
    public List<LogLine> Lines;
    public bool DetailsExpanded;
    public bool RestartAvailable;
    public string LatestVersion;
    public string InstalledVersion;
    public bool Updating;
    public bool SnapshotFailed;
    public Action OnChanged;
    public Action<string, string, ToolTipIcon> OnNotify;
    public Func<RestartPlanData, string> OnPromptRestart;

    private readonly SynchronizationContext uiContext;
    private bool backgroundBusy;

    public TrayModel(TrayConfig config, SynchronizationContext syncContext)
    {
        Config = config;
        uiContext = syncContext;
        Prefs = TrayPreferences.Load(config);
        Accounts = new List<AccountItem>();
        Sources = new HashSet<string>();
        Target = null;
        Running = false;
        Note = "";
        Symbol = "arrow.left.arrow.right";
        Badge = "";
        MoveProgress = null;
        Completion = null;
        Lines = new List<LogLine>();
        DetailsExpanded = false;
        RestartAvailable = false;
        LatestVersion = null;
        InstalledVersion = ReadInstalledVersion(config);
        Updating = false;
        SnapshotFailed = false;
        backgroundBusy = false;
    }

    private static string ReadInstalledVersion(TrayConfig config)
    {
        try
        {
            if (config != null && !string.IsNullOrEmpty(config.Script))
            {
                string pkg = Path.Combine(Path.GetDirectoryName(config.Script), "package.json");
                if (File.Exists(pkg))
                {
                    IDictionary<string, object> d = JsonUtil.CreateSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(pkg, Encoding.UTF8));
                    return JsonUtil.Str(d, "version");
                }
            }
        }
        catch { }
        return null;
    }

    public RecoveryProblemItem RecoveryProblem
    {
        get
        {
            foreach (AccountItem a in Accounts)
            {
                if (a.RecoveryProblem != null) return a.RecoveryProblem;
            }
            return null;
        }
    }

    public List<AccountItem> PendingAccounts
    {
        get
        {
            List<AccountItem> list = new List<AccountItem>();
            foreach (AccountItem a in Accounts)
            {
                if (!string.IsNullOrEmpty(a.Pending)) list.Add(a);
            }
            return list;
        }
    }

    public string PendingMode
    {
        get
        {
            foreach (AccountItem a in Accounts)
            {
                if (!string.IsNullOrEmpty(a.Pending)) return a.Pending;
            }
            return null;
        }
    }

    public bool HasUndoableMove
    {
        get
        {
            foreach (AccountItem a in Accounts)
            {
                if (!string.IsNullOrEmpty(a.Receipt) && a.RecoveryProblem == null) return true;
            }
            return false;
        }
    }

    public bool CanMove
    {
        get
        {
            return !Running && RecoveryProblem == null && PendingAccounts.Count == 0 && Sources.Count > 0 && !string.IsNullOrEmpty(Target) && !Sources.Contains(Target);
        }
    }

    public string Tooltip
    {
        get
        {
            if (RecoveryProblem != null) return "Claude Transplant — Recovery needed";
            if (!string.IsNullOrEmpty(Badge)) return "Claude Transplant — " + Badge + " complete";
            if (PendingAccounts.Count > 0) return "Claude Transplant — Pending sessions";
            return "Claude Transplant";
        }
    }

    public void SelectTarget(string id)
    {
        if (Running) return;
        Target = id;
        if (id != null && Sources.Contains(id)) Sources.Remove(id);
        NotifyChanged();
    }

    public void ToggleSource(string id)
    {
        if (Running || id == null) return;
        if (id == Target) return;
        if (Sources.Contains(id)) Sources.Remove(id);
        else Sources.Add(id);
        NotifyChanged();
    }

    public void ApplyAccounts(List<AccountItem> fresh)
    {
        Accounts = fresh ?? new List<AccountItem>();
        HashSet<string> valid = new HashSet<string>();
        foreach (AccountItem a in Accounts) valid.Add(a.Id);

        if (Target == null || !valid.Contains(Target))
        {
            Target = null;
            foreach (AccountItem a in Accounts)
            {
                if (a.Active) { Target = a.Id; break; }
            }
            if (Target == null && Accounts.Count > 0) Target = Accounts[0].Id;
        }

        List<string> stale = new List<string>();
        foreach (string s in Sources)
        {
            if (!valid.Contains(s) || s == Target) stale.Add(s);
        }
        foreach (string s in stale) Sources.Remove(s);

        if (Sources.Count == 0 && Accounts.Count == 2 && Target != null)
        {
            foreach (AccountItem a in Accounts)
            {
                if (a.Id != Target) Sources.Add(a.Id);
            }
        }
        NotifyChanged();
    }

    public bool LoadAccountsSync()
    {
        if (Config == null)
        {
            SnapshotFailed = true;
            return false;
        }
        int exitCode;
        string stdout;
        string stderr;
        if (!RunCommandSync(new string[] { "accounts", "--json" }, out exitCode, out stdout, out stderr) || exitCode != 0)
        {
            SnapshotFailed = true;
            Note = string.IsNullOrEmpty(stderr) ? "Failed to read accounts" : stderr.Trim();
            return false;
        }
        try
        {
            List<AccountItem> parsed = ParseAccountsJson(stdout);
            ApplyAccounts(parsed);
            return true;
        }
        catch (Exception ex)
        {
            SnapshotFailed = true;
            Note = ex.Message;
            return false;
        }
    }

    public static List<AccountItem> ParseAccountsJson(string json)
    {
        List<AccountItem> list = new List<AccountItem>();
        string trimmed = (json ?? "").Trim();
        if (trimmed.Length == 0) return list;
        string[] lines = trimmed.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string payload = lines[lines.Length - 1];
        object raw = JsonUtil.CreateSerializer().DeserializeObject(payload);
        IEnumerable arr = raw as IEnumerable;
        if (arr == null || raw is string) throw new InvalidOperationException("Invalid accounts JSON");
        foreach (object item in arr)
        {
            IDictionary<string, object> d = item as IDictionary<string, object>;
            if (d != null) list.Add(AccountItem.FromJson(d));
        }
        return list;
    }

    public void RefreshAsync(bool includeSweep)
    {
        if (Running || backgroundBusy || Config == null) return;
        backgroundBusy = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (includeSweep)
                {
                    int sweepExit;
                    string sweepOut;
                    string sweepErr;
                    if (RunCommandSync(new string[] { "sweep", "--json" }, out sweepExit, out sweepOut, out sweepErr))
                    {
                        PostToUi(delegate { HandleSweepOutput(sweepOut); });
                    }
                }
                int code;
                string stdout;
                string stderr;
                if (RunCommandSync(new string[] { "accounts", "--json" }, out code, out stdout, out stderr) && code == 0)
                {
                    List<AccountItem> parsed = ParseAccountsJson(stdout);
                    PostToUi(delegate { ApplyAccounts(parsed); });
                }
            }
            catch { }
            finally
            {
                backgroundBusy = false;
            }
        });
    }

    private void HandleSweepOutput(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return;
        string[] lines = stdout.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            try
            {
                IDictionary<string, object> d = JsonUtil.CreateSerializer().Deserialize<Dictionary<string, object>>(line);
                if (d == null || !(JsonUtil.Bool(d, "swept") ?? false)) continue;
                int moved = JsonUtil.Int(d, "moved") ?? 0;
                bool restart = JsonUtil.Bool(d, "restart") ?? false;
                string err = JsonUtil.Str(d, "error");
                if (moved > 0)
                {
                    string title = string.Format(CultureInfo.InvariantCulture, "{0} held {1} moved", moved, moved == 1 ? "session" : "sessions");
                    Completion = new CompletionBanner(title, restart ? "Restart Claude Desktop to see them" : "History verified");
                    if (restart) RestartAvailable = true;
                    if (OnNotify != null) OnNotify("Claude Transplant", title, ToolTipIcon.Info);
                    NotifyChanged();
                }
                else if (!string.IsNullOrEmpty(err) && Note != err)
                {
                    Note = err;
                    if (OnNotify != null) OnNotify("Claude Transplant", err, ToolTipIcon.Warning);
                    NotifyChanged();
                }
            }
            catch { }
        }
    }

    public void BeginRun()
    {
        Running = true;
        Note = "";
        Completion = null;
        Lines.Clear();
        MoveProgress = new MoveProgressState(NowSeconds());
        Badge = MoveProgress.BadgeText;
        Symbol = "arrow.left.arrow.right";
        NotifyChanged();
    }

    public void StartMove()
    {
        if (!CanMove) return;
        List<string> args = BuildMoveArgs(null, false);
        ExecuteInteractive(args);
    }

    private List<string> BuildMoveArgs(string approvedToken, bool moveOnly)
    {
        List<string> args = new List<string>();
        foreach (AccountItem a in Accounts)
        {
            if (Sources.Contains(a.Id))
            {
                args.Add("--from");
                args.Add(a.Selector);
            }
        }
        AccountItem dest = FindAccount(Target);
        if (dest != null)
        {
            args.Add("--to");
            args.Add(dest.Selector);
        }
        args.Add("--json");
        if (!string.IsNullOrEmpty(approvedToken))
        {
            args.Add("--restart-approved");
            args.Add(approvedToken);
        }
        else if (moveOnly)
        {
            args.Add("--move-only");
        }
        return args;
    }

    public void StartFinish(string approvedToken, bool moveOnly)
    {
        if (Running) return;
        List<string> args = new List<string>();
        args.Add("finish");
        args.Add("--json");
        if (!string.IsNullOrEmpty(approvedToken))
        {
            args.Add("--restart-approved");
            args.Add(approvedToken);
        }
        else if (moveOnly)
        {
            args.Add("--move-only");
        }
        ExecuteInteractive(args);
    }

    public void StartUndo(string approvedToken)
    {
        if (Running) return;
        List<string> args = new List<string>();
        args.Add("undo");
        args.Add("--json");
        if (!string.IsNullOrEmpty(approvedToken))
        {
            args.Add("--restart-approved");
            args.Add(approvedToken);
        }
        ExecuteInteractive(args);
    }

    public void StartKeepLocal(string leaveToken)
    {
        if (Running) return;
        List<string> args = new List<string>();
        args.Add("keep-local");
        args.Add("--json");
        if (!string.IsNullOrEmpty(leaveToken))
        {
            args.Add("--leave-check");
            args.Add(leaveToken);
        }
        ExecuteInteractive(args);
    }

    public void StartRestart(string approvedToken)
    {
        if (Running) return;
        List<string> args = new List<string>();
        args.Add("restart");
        args.Add("--json");
        if (!string.IsNullOrEmpty(approvedToken))
        {
            args.Add("--restart-approved");
            args.Add(approvedToken);
        }
        ExecuteInteractive(args);
    }

    private AccountItem FindAccount(string id)
    {
        foreach (AccountItem a in Accounts)
        {
            if (a.Id == id) return a;
        }
        return null;
    }

    private void ExecuteInteractive(List<string> args)
    {
        if (Config == null) return;
        BeginRun();
        ThreadPool.QueueUserWorkItem(delegate
        {
            RestartPlanData pendingPlan = null;
            string stderrText = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Config.Node;
                psi.Arguments = Config.BuildArguments(args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (Process proc = Process.Start(psi))
                {
                    StringBuilder errBuf = new StringBuilder();
                    proc.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) errBuf.AppendLine(e.Data);
                    };
                    proc.BeginErrorReadLine();
                    string line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        string captured = line;
                        RestartPlanData parsedPlan = TryParsePlan(captured);
                        if (parsedPlan != null) pendingPlan = parsedPlan;
                        PostToUi(delegate { HandleEventLine(captured); });
                    }
                    proc.WaitForExit();
                    stderrText = errBuf.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                stderrText = ex.Message;
            }

            PostToUi(delegate
            {
                Running = false;
                MoveProgress = null;
                Badge = "";
                if (pendingPlan != null)
                {
                    HandleRestartPrompt(args, pendingPlan);
                    return;
                }
                if (!string.IsNullOrEmpty(stderrText) && string.IsNullOrEmpty(Note) && Completion == null)
                {
                    Note = stderrText.Replace("claude-transplant: ", "").Trim();
                    Symbol = "exclamationmark.triangle";
                }
                NotifyChanged();
                RefreshAsync(false);
            });
        });
    }

    private static RestartPlanData TryParsePlan(string line)
    {
        try
        {
            IDictionary<string, object> d = JsonUtil.CreateSerializer().Deserialize<Dictionary<string, object>>(line);
            if (d != null && (JsonUtil.Bool(d, "plan") ?? false))
            {
                return RestartPlanData.FromEvent(d);
            }
        }
        catch { }
        return null;
    }

    private void HandleRestartPrompt(List<string> originalArgs, RestartPlanData plan)
    {
        string choice = "cancel";
        if (Prefs.AutoApproveRestart && !string.IsNullOrEmpty(plan.Token))
        {
            choice = "approve";
        }
        else if (OnPromptRestart != null)
        {
            choice = OnPromptRestart(plan);
        }
        if (choice == "approve" && !string.IsNullOrEmpty(plan.Token))
        {
            if (originalArgs.Count > 0 && originalArgs[0] == "undo") StartUndo(plan.Token);
            else if (originalArgs.Count > 0 && originalArgs[0] == "finish") StartFinish(plan.Token, false);
            else if (originalArgs.Count > 0 && originalArgs[0] == "restart") StartRestart(plan.Token);
            else ExecuteInteractive(BuildMoveArgs(plan.Token, false));
        }
        else if (choice == "move-only")
        {
            if (originalArgs.Count > 0 && originalArgs[0] == "finish") StartFinish(null, true);
            else ExecuteInteractive(BuildMoveArgs(null, true));
        }
        else
        {
            Note = "Operation cancelled before restarting Claude Desktop";
            NotifyChanged();
        }
    }

    public void HandleEventLine(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return;
        IDictionary<string, object> d;
        try
        {
            d = JsonUtil.CreateSerializer().Deserialize<Dictionary<string, object>>(raw);
        }
        catch { return; }
        if (d == null) return;
        if (JsonUtil.Bool(d, "plan") ?? false) return;

        string stage = JsonUtil.Str(d, "stage");
        string text = JsonUtil.Str(d, "text");
        bool live = JsonUtil.Bool(d, "live") ?? false;
        int? completed = JsonUtil.Int(d, "completed");
        int? total = JsonUtil.Int(d, "total");
        int? sessionTotal = JsonUtil.Int(d, "sessionTotal");
        int? remainingCount = JsonUtil.Int(d, "remainingCount");

        if (!string.IsNullOrEmpty(stage))
        {
            if (MoveProgress == null) MoveProgress = new MoveProgressState(NowSeconds());
            if (sessionTotal.HasValue && remainingCount.HasValue)
            {
                MoveProgress.SetSessions(sessionTotal.Value, remainingCount.Value);
            }
            if (completed.HasValue && total.HasValue)
            {
                MoveProgress.Update(stage, completed.Value, total.Value, NowSeconds());
            }
            string activity = JsonUtil.Str(d, "activity");
            if (!string.IsNullOrEmpty(activity)) MoveProgress.Activity = activity;
            Badge = MoveProgress.BadgeText;
            if (!live && !string.IsNullOrEmpty(text))
            {
                Lines.Add(new LogLine(stage, text));
            }
            NotifyChanged();
            return;
        }

        if (JsonUtil.Str(d, "undone") != null)
        {
            int count = JsonUtil.Int(d, "sessions") ?? 0;
            bool restart = JsonUtil.Bool(d, "restart") ?? false;
            string title = string.Format(CultureInfo.InvariantCulture, "Undid move ({0} {1} restored)", count, count == 1 ? "session" : "sessions");
            Completion = new CompletionBanner(title, restart ? "Restart Claude Desktop to refresh" : "Source sessions restored");
            RestartAvailable = restart;
            Note = "";
            Symbol = "checkmark";
            NotifyChanged();
            return;
        }

        List<string> refused = JsonUtil.StrList(d, "refused");
        if (refused.Count > 0)
        {
            Note = JsonUtil.Str(d, "reason") ?? refused[0];
            Symbol = "exclamationmark.triangle";
            NotifyChanged();
            return;
        }

        if (JsonUtil.Bool(d, "nothing") ?? false)
        {
            Note = "Nothing to move";
            Symbol = "arrow.left.arrow.right";
            NotifyChanged();
            return;
        }

        if (JsonUtil.Bool(d, "done") ?? false)
        {
            bool ok = JsonUtil.Bool(d, "ok") ?? false;
            int moved = JsonUtil.Int(d, "moved") ?? 0;
            int heldCount = JsonUtil.Int(d, "heldCount") ?? 0;
            bool restart = JsonUtil.Bool(d, "restart") ?? false;
            bool restarted = JsonUtil.Bool(d, "restarted") ?? false;
            int? keptLocal = JsonUtil.Int(d, "keptLocal");
            int? heldCancelled = JsonUtil.Int(d, "heldCancelled");
            string reason = JsonUtil.Str(d, "reason");
            string note = JsonUtil.Str(d, "note");

            RestartAvailable = restart && !restarted;
            if (keptLocal.HasValue || heldCancelled.HasValue)
            {
                Completion = new CompletionBanner("Kept completed sessions", "Pending checks cleared");
                Note = "";
                Symbol = "checkmark";
            }
            else if (ok)
            {
                if (moved > 0)
                {
                    string title = string.Format(CultureInfo.InvariantCulture, "{0} {1} moved", moved, moved == 1 ? "session" : "sessions");
                    string detail = heldCount > 0
                        ? string.Format(CultureInfo.InvariantCulture, "{0} open {1} held until closed", heldCount, heldCount == 1 ? "session" : "sessions")
                        : (restarted ? "Claude Desktop reopened" : (restart ? "Restart Claude Desktop to see them" : "History verified"));
                    Completion = new CompletionBanner(title, detail);
                    Note = title;
                    Symbol = "checkmark";
                }
                else if (restarted)
                {
                    Completion = new CompletionBanner("Claude Desktop restarted", "Session list refreshed");
                    Note = "";
                    Symbol = "checkmark";
                }
                else
                {
                    Note = note ?? reason ?? "Up to date";
                    Symbol = "checkmark";
                }
            }
            else
            {
                List<IDictionary<string, object>> failed = JsonUtil.ObjList(d, "failed");
                if (!string.IsNullOrEmpty(reason))
                {
                    Note = reason;
                }
                else if (failed.Count > 0)
                {
                    string fTitle = JsonUtil.Str(failed[0], "title") ?? JsonUtil.Str(failed[0], "id") ?? "Session";
                    string fErr = JsonUtil.Str(failed[0], "error") ?? "move blocked";
                    Note = fTitle + ": " + fErr;
                }
                else
                {
                    Note = note ?? "Move needs attention";
                }
                Symbol = "exclamationmark.triangle";
            }
            NotifyChanged();
        }
    }

    public void CheckForUpdatesAsync()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://registry.npmjs.org/claude-transplant/latest");
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.UserAgent = "claude-transplant-tray";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    IDictionary<string, object> d = JsonUtil.CreateSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    string latest = JsonUtil.Str(d, "version");
                    if (!string.IsNullOrEmpty(latest) && !string.IsNullOrEmpty(InstalledVersion) && IsNewerSemver(latest, InstalledVersion))
                    {
                        PostToUi(delegate
                        {
                            LatestVersion = latest;
                            NotifyChanged();
                        });
                    }
                }
            }
            catch { }
        });
    }

    public void InstallUpdate()
    {
        if (Updating || string.IsNullOrEmpty(LatestVersion)) return;
        Updating = true;
        Note = "Updating to v" + LatestVersion + "...";
        NotifyChanged();
        string targetVer = LatestVersion;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d /s /c \"npx.cmd -y claude-transplant@" + targetVer + " menubar\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                if (p != null) p.WaitForExit(120000);
            }
            catch (Exception ex)
            {
                PostToUi(delegate
                {
                    Updating = false;
                    Note = "Update failed: " + ex.Message;
                    NotifyChanged();
                });
            }
        });
    }

    private static bool IsNewerSemver(string candidate, string current)
    {
        int[] a = ParseVer(candidate);
        int[] b = ParseVer(current);
        if (a == null || b == null) return false;
        for (int i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    private static int[] ParseVer(string v)
    {
        string clean = (v ?? "").Trim().TrimStart('v');
        int dash = clean.IndexOf('-');
        if (dash >= 0) clean = clean.Substring(0, dash);
        string[] parts = clean.Split('.');
        if (parts.Length < 3) return null;
        int major;
        int minor;
        int patch;
        if (!int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor) || !int.TryParse(parts[2], out patch)) return null;
        return new int[] { major, minor, patch };
    }

    private bool RunCommandSync(IList<string> args, out int exitCode, out string stdout, out string stderr)
    {
        exitCode = -1;
        stdout = "";
        stderr = "";
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Config.Node;
            psi.Arguments = Config.BuildArguments(args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (Process proc = Process.Start(psi))
            {
                StringBuilder errBuf = new StringBuilder();
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) errBuf.AppendLine(e.Data);
                };
                proc.BeginErrorReadLine();
                stdout = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); } catch { }
                    stderr = "Timed out talking to claude-transplant";
                    return false;
                }
                exitCode = proc.ExitCode;
                stderr = errBuf.ToString();
                return true;
            }
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return false;
        }
    }

    private static double NowSeconds()
    {
        return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    private void PostToUi(Action action)
    {
        if (uiContext != null)
        {
            uiContext.Post(delegate { action(); }, null);
        }
        else
        {
            action();
        }
    }

    public void NotifyChanged()
    {
        if (OnChanged != null) OnChanged();
    }
}

internal sealed class HitRegion
{
    public RectangleF Bounds;
    public string Kind;
    public string Value;

    public HitRegion(RectangleF bounds, string kind, string value)
    {
        Bounds = bounds;
        Kind = kind;
        Value = value;
    }
}

internal static class PanelRenderer
{
    private const float PanelWidth = 540f;
    private const float Padding = 16f;
    private const float ColumnWidth = 224f;
    private const float GapWidth = 60f;
    private const float RowHeight = 52f;
    private const float RowSpacing = 6f;

    private static readonly Color BgColor = Color.FromArgb(28, 28, 30);
    private static readonly Color CardBg = Color.FromArgb(40, 40, 44);
    private static readonly Color CardHoverBg = Color.FromArgb(50, 50, 56);
    private static readonly Color CardSelectedBg = Color.FromArgb(42, 54, 74);
    private static readonly Color BorderSubtle = Color.FromArgb(62, 62, 68);
    private static readonly Color AccentBlue = Color.FromArgb(10, 132, 255);
    private static readonly Color AccentGreen = Color.FromArgb(48, 209, 88);
    private static readonly Color AccentAmber = Color.FromArgb(255, 159, 10);
    private static readonly Color AccentRed = Color.FromArgb(255, 69, 58);
    private static readonly Color TextPrimary = Color.FromArgb(245, 245, 247);
    private static readonly Color TextSecondary = Color.FromArgb(158, 158, 166);
    private static readonly Color TextMuted = Color.FromArgb(110, 110, 118);

    public static SizeF MeasurePanel(TrayModel model)
    {
        List<HitRegion> dummy = new List<HitRegion>();
        using (Bitmap bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            float h = LayoutAndDraw(g, model, false, null, dummy);
            return new SizeF(PanelWidth, h);
        }
    }

    public static Bitmap RenderBitmap(TrayModel model, float scale, SizeF? fixedLogicalSize, string hoveredKey, List<HitRegion> regions)
    {
        SizeF measured = fixedLogicalSize.HasValue ? fixedLogicalSize.Value : MeasurePanel(model);
        int pxW = Math.Max(1, (int)Math.Ceiling(measured.Width * scale));
        int pxH = Math.Max(1, (int)Math.Ceiling(measured.Height * scale));
        Bitmap bmp = new Bitmap(pxW, pxH, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(BgColor);
            g.ScaleTransform(scale, scale);
            LayoutAndDraw(g, model, true, hoveredKey, regions);
            if (fixedLogicalSize.HasValue)
            {
                using (Pen borderPen = new Pen(BorderSubtle, 1f))
                {
                    g.DrawRectangle(borderPen, 0.5f, 0.5f, measured.Width - 1f, measured.Height - 1f);
                }
            }
        }
        return bmp;
    }

    public static float LayoutAndDraw(Graphics g, TrayModel model, bool draw, string hoveredKey, List<HitRegion> regions)
    {
        regions.Clear();
        float y = Padding;

        using (Font titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point))
        using (Font sectionFont = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point))
        using (Font nameFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
        using (Font subFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
        using (Font tagFont = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point))
        using (Font btnFont = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point))
        using (Font monoFont = new Font("Consolas", 8f, FontStyle.Regular, GraphicsUnit.Point))
        {
            if (draw)
            {
                using (Brush b = new SolidBrush(TextPrimary))
                {
                    g.DrawString("Claude Transplant", titleFont, b, Padding, y);
                }
                if (!string.IsNullOrEmpty(model.InstalledVersion))
                {
                    SizeF titleSz = g.MeasureString("Claude Transplant", titleFont);
                    using (Brush b = new SolidBrush(TextMuted))
                    {
                        g.DrawString("v" + model.InstalledVersion, subFont, b, Padding + titleSz.Width + 4f, y + 3f);
                    }
                }
            }

            if (!string.IsNullOrEmpty(model.LatestVersion))
            {
                string upText = model.Updating ? "Updating..." : ("Update to " + model.LatestVersion);
                SizeF upSz = g.MeasureString(upText, tagFont);
                RectangleF upRect = new RectangleF(PanelWidth - Padding - upSz.Width - 16f, y, upSz.Width + 16f, 22f);
                regions.Add(new HitRegion(upRect, "update", model.LatestVersion));
                if (draw)
                {
                    DrawPillButton(g, upRect, upText, tagFont, Color.FromArgb(40, 10, 132, 255), AccentBlue, hoveredKey == "update:" + model.LatestVersion);
                }
            }
            y += 28f;

            RecoveryProblemItem recovery = model.RecoveryProblem;
            if (recovery != null)
            {
                y = DrawBanner(g, draw, y, AccentRed, "Move recovery needed", recovery.Error, nameFont, subFont);
                y += 8f;
            }

            if (model.Accounts.Count == 0)
            {
                RectangleF emptyRect = new RectangleF(Padding, y, PanelWidth - Padding * 2f, 58f);
                if (draw)
                {
                    using (GraphicsPath path = RoundRect(emptyRect, 8f))
                    using (Brush bg = new SolidBrush(CardBg))
                    {
                        g.FillPath(bg, path);
                    }
                    using (Brush b = new SolidBrush(TextSecondary))
                    {
                        StringFormat sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("No Claude Desktop accounts found yet.\nSign in to Claude Desktop and start a Code session.", subFont, b, emptyRect, sf);
                    }
                }
                y += 66f;
            }
            else
            {
                float leftX = Padding;
                float rightX = Padding + ColumnWidth + GapWidth;
                if (draw)
                {
                    using (Brush b = new SolidBrush(TextSecondary))
                    {
                        g.DrawString("FROM", sectionFont, b, leftX, y);
                        g.DrawString("TO", sectionFont, b, rightX, y);
                    }
                }
                y += 18f;

                Dictionary<string, float> sourceCenters = new Dictionary<string, float>();
                float targetCenter = -1f;

                float rowsTop = y;
                for (int i = 0; i < model.Accounts.Count; i++)
                {
                    AccountItem acct = model.Accounts[i];
                    float rowY = rowsTop + i * (RowHeight + RowSpacing);
                    RectangleF fromRect = new RectangleF(leftX, rowY, ColumnWidth, RowHeight);
                    RectangleF toRect = new RectangleF(rightX, rowY, ColumnWidth, RowHeight);

                    bool isTarget = acct.Id == model.Target;
                    bool isSource = model.Sources.Contains(acct.Id);
                    bool pendingRow = !string.IsNullOrEmpty(acct.Pending);

                    if (!isTarget && !model.Running)
                    {
                        regions.Add(new HitRegion(fromRect, "source", acct.Id));
                    }
                    if (!model.Running)
                    {
                        regions.Add(new HitRegion(toRect, "target", acct.Id));
                    }

                    if (isSource || pendingRow)
                    {
                        sourceCenters[acct.Id] = rowY + RowHeight * 0.5f;
                    }
                    if (isTarget)
                    {
                        targetCenter = rowY + RowHeight * 0.5f;
                    }

                    if (draw)
                    {
                        DrawAccountCard(g, fromRect, acct, true, isSource, isTarget, hoveredKey == "source:" + acct.Id, nameFont, subFont, tagFont);
                        DrawAccountCard(g, toRect, acct, false, isTarget, false, hoveredKey == "target:" + acct.Id, nameFont, subFont, tagFont);
                    }
                }

                if (draw && targetCenter > 0f && sourceCenters.Count > 0)
                {
                    float startX = leftX + ColumnWidth + 4f;
                    float endX = rightX - 4f;
                    foreach (KeyValuePair<string, float> kv in sourceCenters)
                    {
                        AccountItem srcAcct = null;
                        foreach (AccountItem candidate in model.Accounts)
                        {
                            if (candidate.Id == kv.Key) { srcAcct = candidate; break; }
                        }
                        bool isPendingArrow = srcAcct != null && !string.IsNullOrEmpty(srcAcct.Pending);
                        Color arrowColor = isPendingArrow ? AccentAmber : AccentBlue;
                        DrawBezierConnector(g, new PointF(startX, kv.Value), new PointF(endX, targetCenter), arrowColor, isPendingArrow);
                    }
                }

                y = rowsTop + model.Accounts.Count * RowHeight + Math.Max(0, model.Accounts.Count - 1) * RowSpacing + 12f;
            }

            List<AccountItem> pendingList = model.PendingAccounts;
            if (pendingList.Count > 0)
            {
                foreach (AccountItem p in pendingList)
                {
                    string pTitle = p.Name + " · " + p.Plan;
                    string pDetail = DescribePending(p);
                    y = DrawPendingRow(g, draw, y, p, pTitle, pDetail, nameFont, subFont, tagFont, hoveredKey, regions);
                    y += 6f;
                }
            }

            if (model.MoveProgress != null)
            {
                RectangleF barOuter = new RectangleF(Padding, y, PanelWidth - Padding * 2f, 40f);
                if (draw)
                {
                    using (GraphicsPath p = RoundRect(barOuter, 8f))
                    using (Brush bg = new SolidBrush(CardBg))
                    {
                        g.FillPath(bg, p);
                    }
                    using (Brush b = new SolidBrush(TextPrimary))
                    {
                        g.DrawString(model.MoveProgress.LabelText, subFont, b, Padding + 10f, y + 6f);
                    }
                    using (Brush b = new SolidBrush(TextSecondary))
                    {
                        StringFormat rightAlign = new StringFormat();
                        rightAlign.Alignment = StringAlignment.Far;
                        g.DrawString(model.MoveProgress.BadgeText, subFont, b, new RectangleF(Padding, y + 6f, PanelWidth - Padding * 2f - 10f, 16f), rightAlign);
                    }
                    RectangleF track = new RectangleF(Padding + 10f, y + 26f, PanelWidth - Padding * 2f - 20f, 6f);
                    using (GraphicsPath tp = RoundRect(track, 3f))
                    using (Brush tb = new SolidBrush(Color.FromArgb(62, 62, 68)))
                    {
                        g.FillPath(tb, tp);
                    }
                    float fillW = Math.Max(6f, track.Width * (float)model.MoveProgress.Fraction);
                    RectangleF fill = new RectangleF(track.X, track.Y, Math.Min(track.Width, fillW), track.Height);
                    using (GraphicsPath fp = RoundRect(fill, 3f))
                    using (Brush fb = new SolidBrush(AccentBlue))
                    {
                        g.FillPath(fb, fp);
                    }
                }
                y += 48f;
            }
            else if (model.Completion != null)
            {
                y = DrawBanner(g, draw, y, AccentGreen, model.Completion.Title, model.Completion.Detail, nameFont, subFont);
                y += 8f;
            }
            else if (!string.IsNullOrEmpty(model.Note))
            {
                Color tone = model.Symbol == "exclamationmark.triangle" ? AccentAmber : TextSecondary;
                y = DrawBanner(g, draw, y, tone, model.Note, null, nameFont, subFont);
                y += 8f;
            }

            if (model.Lines.Count > 0)
            {
                RectangleF detBtn = new RectangleF(Padding, y, 90f, 20f);
                regions.Add(new HitRegion(detBtn, "details", "toggle"));
                if (draw)
                {
                    string glyph = model.DetailsExpanded ? "▾ Details" : "▸ Details";
                    using (Brush b = new SolidBrush(hoveredKey == "details:toggle" ? TextPrimary : TextSecondary))
                    {
                        g.DrawString(glyph, subFont, b, Padding, y + 2f);
                    }
                }
                y += 22f;
                if (model.DetailsExpanded)
                {
                    float boxH = model.Lines.Count * 18f + 12f;
                    RectangleF logBox = new RectangleF(Padding, y, PanelWidth - Padding * 2f, boxH);
                    if (draw)
                    {
                        using (GraphicsPath lp = RoundRect(logBox, 6f))
                        using (Brush lb = new SolidBrush(Color.FromArgb(20, 20, 22)))
                        {
                            g.FillPath(lb, lp);
                        }
                        for (int i = 0; i < model.Lines.Count; i++)
                        {
                            LogLine line = model.Lines[i];
                            float ly = y + 6f + i * 18f;
                            using (Brush sb = new SolidBrush(TextMuted))
                            using (Brush tb = new SolidBrush(TextSecondary))
                            {
                                g.DrawString(line.Stage, monoFont, sb, Padding + 8f, ly);
                                g.DrawString(Truncate(line.Text, 58), monoFont, tb, Padding + 84f, ly);
                            }
                        }
                    }
                    y += boxH + 8f;
                }
            }

            y += 4f;
            float btnX = Padding;
            float btnH = 28f;

            if (recovery != null || pendingList.Count > 0)
            {
                string finishLabel = model.PendingMode == "undo" ? "Finish Undo" : "Finish";
                btnX = AddActionButton(g, draw, btnX, y, btnH, finishLabel, "finish", "run", !model.Running, true, btnFont, hoveredKey, regions);
                if (pendingList.Count > 0 && model.PendingMode != "undo")
                {
                    btnX = AddActionButton(g, draw, btnX, y, btnH, "Keep Moved", "keep-local", "", !model.Running, false, btnFont, hoveredKey, regions);
                }
            }
            else
            {
                btnX = AddActionButton(g, draw, btnX, y, btnH, "Move", "move", "run", model.CanMove, true, btnFont, hoveredKey, regions);
            }

            if (model.HasUndoableMove && model.PendingMode != "undo")
            {
                btnX = AddActionButton(g, draw, btnX, y, btnH, "Undo", "undo", "run", !model.Running, false, btnFont, hoveredKey, regions);
            }

            if (model.RestartAvailable)
            {
                btnX = AddActionButton(g, draw, btnX, y, btnH, "Restart Claude", "restart", "run", !model.Running, false, btnFont, hoveredKey, regions);
            }

            SizeF quitSz = g.MeasureString("Quit", btnFont);
            RectangleF quitRect = new RectangleF(PanelWidth - Padding - quitSz.Width - 20f, y, quitSz.Width + 20f, btnH);
            regions.Add(new HitRegion(quitRect, "quit", "app"));
            if (draw)
            {
                DrawPillButton(g, quitRect, "Quit", btnFont, CardBg, TextSecondary, hoveredKey == "quit:app");
            }

            y += btnH + Padding;
        }
        return y;
    }

    private static string DescribePending(AccountItem p)
    {
        int n = p.PendingCount ?? 0;
        if (p.PendingState == "open")
        {
            return n > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} open {1} waiting for idle close", n, n == 1 ? "session" : "sessions")
                : "Waiting for open session to close";
        }
        if (p.PendingState == "external") return "Waiting for external Claude Code CLI session to exit";
        if (p.PendingState == "held") return "Held until Claude Desktop restarts";
        if (p.PendingState == "undo" || p.PendingState == "undo-login") return "Undo waiting to complete";
        if (p.PendingAction == "restart") return "Restart Claude Desktop to finish moving held sessions";
        return "Pending continuation";
    }

    private static float DrawPendingRow(Graphics g, bool draw, float y, AccountItem acct, string title, string detail, Font titleFont, Font subFont, Font tagFont, string hoveredKey, List<HitRegion> regions)
    {
        RectangleF box = new RectangleF(Padding, y, PanelWidth - Padding * 2f, 44f);
        if (!string.IsNullOrEmpty(acct.PendingCancel))
        {
            RectangleF leaveRect = new RectangleF(box.Right - 64f, y + 10f, 54f, 24f);
            regions.Add(new HitRegion(leaveRect, "keep-local", acct.PendingCancel));
            if (draw)
            {
                using (GraphicsPath gp = RoundRect(box, 8f))
                using (Brush bg = new SolidBrush(Color.FromArgb(38, 255, 159, 10)))
                {
                    g.FillPath(bg, gp);
                }
                using (Brush tb = new SolidBrush(TextPrimary))
                using (Brush sb = new SolidBrush(TextSecondary))
                {
                    g.DrawString(title, titleFont, tb, box.X + 10f, y + 6f);
                    g.DrawString(detail, subFont, sb, box.X + 10f, y + 23f);
                }
                DrawPillButton(g, leaveRect, "Leave", tagFont, CardBg, TextSecondary, hoveredKey == "keep-local:" + acct.PendingCancel);
            }
        }
        else if (draw)
        {
            using (GraphicsPath gp = RoundRect(box, 8f))
            using (Brush bg = new SolidBrush(Color.FromArgb(32, 255, 159, 10)))
            {
                g.FillPath(bg, gp);
            }
            using (Brush tb = new SolidBrush(TextPrimary))
            using (Brush sb = new SolidBrush(TextSecondary))
            {
                g.DrawString(title, titleFont, tb, box.X + 10f, y + 6f);
                g.DrawString(detail, subFont, sb, box.X + 10f, y + 23f);
            }
        }
        return y + 44f;
    }

    private static float DrawBanner(Graphics g, bool draw, float y, Color accent, string title, string detail, Font titleFont, Font subFont)
    {
        float h = string.IsNullOrEmpty(detail) ? 32f : 46f;
        RectangleF box = new RectangleF(Padding, y, PanelWidth - Padding * 2f, h);
        if (draw)
        {
            using (GraphicsPath gp = RoundRect(box, 8f))
            using (Brush bg = new SolidBrush(Color.FromArgb(30, accent.R, accent.G, accent.B)))
            using (Pen border = new Pen(Color.FromArgb(90, accent.R, accent.G, accent.B), 1f))
            {
                g.FillPath(bg, gp);
                g.DrawPath(border, gp);
            }
            using (Brush tb = new SolidBrush(TextPrimary))
            {
                g.DrawString(Truncate(title, 68), titleFont, tb, box.X + 10f, y + (string.IsNullOrEmpty(detail) ? 7f : 6f));
            }
            if (!string.IsNullOrEmpty(detail))
            {
                using (Brush sb = new SolidBrush(TextSecondary))
                {
                    g.DrawString(Truncate(detail, 76), subFont, sb, box.X + 10f, y + 24f);
                }
            }
        }
        return y + h;
    }

    private static float AddActionButton(Graphics g, bool draw, float x, float y, float h, string text, string kind, string val, bool enabled, bool primary, Font font, string hoveredKey, List<HitRegion> regions)
    {
        SizeF sz = g.MeasureString(text, font);
        RectangleF rect = new RectangleF(x, y, sz.Width + 24f, h);
        if (enabled) regions.Add(new HitRegion(rect, kind, val));
        if (draw)
        {
            Color bg = primary
                ? (enabled ? AccentBlue : Color.FromArgb(48, 52, 62))
                : (enabled ? CardBg : Color.FromArgb(34, 34, 38));
            Color fg = enabled ? TextPrimary : TextMuted;
            DrawPillButton(g, rect, text, font, bg, fg, enabled && hoveredKey == kind + ":" + val);
        }
        return rect.Right + 8f;
    }

    private static void DrawPillButton(Graphics g, RectangleF rect, string text, Font font, Color bg, Color fg, bool hovered)
    {
        Color fill = hovered
            ? Color.FromArgb(bg.A, Math.Min(255, bg.R + 18), Math.Min(255, bg.G + 18), Math.Min(255, bg.B + 18))
            : bg;
        using (GraphicsPath gp = RoundRect(rect, 6f))
        using (Brush b = new SolidBrush(fill))
        using (Pen p = new Pen(BorderSubtle, 1f))
        {
            g.FillPath(b, gp);
            g.DrawPath(p, gp);
        }
        using (Brush tb = new SolidBrush(fg))
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            g.DrawString(text, font, tb, rect, sf);
        }
    }

    private static void DrawAccountCard(Graphics g, RectangleF rect, AccountItem acct, bool isFromColumn, bool selected, bool disabled, bool hovered, Font nameFont, Font subFont, Font tagFont)
    {
        Color bg = selected ? CardSelectedBg : (hovered && !disabled ? CardHoverBg : CardBg);
        Color border = selected ? AccentBlue : BorderSubtle;
        using (GraphicsPath gp = RoundRect(rect, 8f))
        using (Brush b = new SolidBrush(bg))
        using (Pen p = new Pen(border, selected ? 1.4f : 1f))
        {
            g.FillPath(b, gp);
            g.DrawPath(p, gp);
        }

        float indX = rect.X + 10f;
        float indY = rect.Y + (rect.Height - 14f) * 0.5f;
        RectangleF indRect = new RectangleF(indX, indY, 14f, 14f);
        if (isFromColumn)
        {
            using (GraphicsPath cp = RoundRect(indRect, 3.5f))
            {
                if (selected)
                {
                    using (Brush b = new SolidBrush(AccentBlue)) g.FillPath(b, cp);
                    using (Pen checkPen = new Pen(Color.White, 1.8f))
                    {
                        g.DrawLines(checkPen, new PointF[]
                        {
                            new PointF(indX + 3.2f, indY + 7.2f),
                            new PointF(indX + 6.0f, indY + 10.2f),
                            new PointF(indX + 11.0f, indY + 4.2f)
                        });
                    }
                }
                else
                {
                    using (Pen p = new Pen(disabled ? TextMuted : TextSecondary, 1.2f)) g.DrawPath(p, cp);
                }
            }
        }
        else
        {
            if (selected)
            {
                using (Pen p = new Pen(AccentBlue, 1.6f)) g.DrawEllipse(p, indRect);
                RectangleF dot = new RectangleF(indX + 3.5f, indY + 3.5f, 7f, 7f);
                using (Brush b = new SolidBrush(AccentBlue)) g.FillEllipse(b, dot);
            }
            else
            {
                using (Pen p = new Pen(TextSecondary, 1.2f)) g.DrawEllipse(p, indRect);
            }
        }

        float textX = indX + 22f;
        float maxTextW = rect.Right - textX - 8f;

        string badge = null;
        Color badgeColor = AccentGreen;
        if (!string.IsNullOrEmpty(acct.Pending) && isFromColumn)
        {
            badge = acct.PendingAction == "restart" ? "restart" : "waiting";
            badgeColor = AccentAmber;
        }
        else if (acct.Active)
        {
            badge = "active";
            badgeColor = AccentGreen;
        }

        if (badge != null)
        {
            SizeF bSz = g.MeasureString(badge, tagFont);
            RectangleF bRect = new RectangleF(rect.Right - bSz.Width - 14f, rect.Y + 7f, bSz.Width + 8f, 15f);
            maxTextW = bRect.X - textX - 4f;
            using (GraphicsPath bp = RoundRect(bRect, 4f))
            using (Brush bb = new SolidBrush(Color.FromArgb(36, badgeColor.R, badgeColor.G, badgeColor.B)))
            using (Brush tf = new SolidBrush(badgeColor))
            {
                g.FillPath(bb, bp);
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(badge, tagFont, tf, bRect, sf);
            }
        }

        Color primary = disabled ? TextMuted : TextPrimary;
        Color secondary = disabled ? TextMuted : TextSecondary;
        using (Brush nb = new SolidBrush(primary))
        using (Brush sb = new SolidBrush(secondary))
        {
            StringFormat ellipsis = new StringFormat();
            ellipsis.Trimming = StringTrimming.EllipsisCharacter;
            ellipsis.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(acct.Name, nameFont, nb, new RectangleF(textX, rect.Y + 6f, Math.Max(20f, maxTextW), 18f), ellipsis);
            string sub = acct.Plan;
            string meta = acct.MetaLine;
            if (!string.IsNullOrEmpty(meta)) sub = sub + " · " + meta;
            g.DrawString(sub, subFont, sb, new RectangleF(textX, rect.Y + 26f, rect.Right - textX - 8f, 18f), ellipsis);
        }
    }

    private static void DrawBezierConnector(Graphics g, PointF start, PointF end, Color color, bool dashed)
    {
        float dx = (end.X - start.X) * 0.55f;
        PointF c1 = new PointF(start.X + dx, start.Y);
        PointF c2 = new PointF(end.X - dx, end.Y);
        using (Pen pen = new Pen(color, 2.0f))
        {
            if (dashed) pen.DashPattern = new float[] { 3f, 2f };
            g.DrawBezier(pen, start, c1, c2, new PointF(end.X - 4f, end.Y));
        }
        using (Brush b = new SolidBrush(color))
        {
            PointF[] head = new PointF[]
            {
                end,
                new PointF(end.X - 6.5f, end.Y - 4f),
                new PointF(end.X - 6.5f, end.Y + 4f)
            };
            g.FillPolygon(b, head);
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, max - 1) + "…";
    }

    internal static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = radius * 2f;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 180f, 90f);
        path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
        path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }
}

internal static class TrayIconFactory
{
    public static Icon Create(string symbol, string badge)
    {
        using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (!string.IsNullOrEmpty(badge))
            {
                using (GraphicsPath pill = PanelRenderer.RoundRect(new RectangleF(1f, 5f, 30f, 22f), 6f))
                using (Brush bg = new SolidBrush(Color.FromArgb(10, 132, 255)))
                {
                    g.FillPath(bg, pill);
                }
                using (Font f = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point))
                using (Brush b = new SolidBrush(Color.White))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(badge, f, b, new RectangleF(0f, 5f, 32f, 22f), sf);
                }
            }
            else if (symbol == "exclamationmark.triangle")
            {
                PointF[] tri = new PointF[] { new PointF(16f, 3f), new PointF(30f, 28f), new PointF(2f, 28f) };
                using (Brush b = new SolidBrush(Color.FromArgb(255, 159, 10))) g.FillPolygon(b, tri);
                using (Pen p = new Pen(Color.FromArgb(28, 28, 30), 2.8f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawLine(p, 16f, 11f, 16f, 19f);
                }
                using (Brush dot = new SolidBrush(Color.FromArgb(28, 28, 30))) g.FillEllipse(dot, 14.3f, 22.5f, 3.4f, 3.4f);
            }
            else if (symbol == "checkmark")
            {
                using (Brush bg = new SolidBrush(Color.FromArgb(48, 209, 88))) g.FillEllipse(bg, 2f, 2f, 28f, 28f);
                using (Pen p = new Pen(Color.White, 3.2f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new PointF[] { new PointF(9f, 16.5f), new PointF(14f, 21.5f), new PointF(23f, 11f) });
                }
            }
            else
            {
                using (Pen p = new Pen(Color.White, 2.6f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    g.DrawLine(p, 5f, 11f, 26f, 11f);
                    g.DrawLines(p, new PointF[] { new PointF(20f, 6f), new PointF(26f, 11f), new PointF(20f, 16f) });
                    g.DrawLine(p, 27f, 21f, 6f, 21f);
                    g.DrawLines(p, new PointF[] { new PointF(12f, 16f), new PointF(6f, 21f), new PointF(12f, 26f) });
                }
            }

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(hIcon).Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
    }
}

internal sealed class RestartDialogForm : Form
{
    public string Choice;
    public bool RememberChoice;

    public RestartDialogForm(RestartPlanData plan)
    {
        Choice = "cancel";
        RememberChoice = false;
        Text = "Restart Claude Desktop?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 36);
        ForeColor = Color.FromArgb(245, 245, 247);
        ClientSize = new Size(460, 290);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        Label header = new Label();
        header.Text = plan.Kind == "undo"
            ? "Claude Desktop must close briefly to restore scheduled tasks or sessions."
            : (plan.Kind == "restart"
                ? "Claude Desktop will close and reopen to refresh your session list."
                : "Claude Desktop has open sessions that must close briefly to complete this move.");
        header.Location = new Point(18, 16);
        header.Size = new Size(424, 42);
        header.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        Controls.Add(header);

        StringBuilder details = new StringBuilder();
        if (plan.Affected.Count > 0)
        {
            details.AppendLine("Open Claude Code sessions:");
            int shown = Math.Min(5, plan.Affected.Count);
            for (int i = 0; i < shown; i++)
            {
                RestartWorkerItem w = plan.Affected[i];
                string name = !string.IsNullOrEmpty(w.Title) ? w.Title : (!string.IsNullOrEmpty(w.Cwd) ? Path.GetFileName(w.Cwd) : ("PID " + w.Pid));
                details.AppendLine("  • " + name);
            }
            if (plan.Affected.Count > shown)
            {
                details.AppendLine(string.Format(CultureInfo.InvariantCulture, "  • +{0} more", plan.Affected.Count - shown));
            }
        }
        else if (plan.Held.Count > 0)
        {
            details.AppendLine("Held sessions:");
            int shown = Math.Min(5, plan.Held.Count);
            for (int i = 0; i < shown; i++)
            {
                details.AppendLine("  • " + plan.Held[i].Title);
            }
        }
        else
        {
            details.AppendLine("No active Code workers will be interrupted. Claude Desktop will reopen automatically.");
        }

        Label body = new Label();
        body.Text = details.ToString().TrimEnd();
        body.Location = new Point(18, 64);
        body.Size = new Size(424, 126);
        body.ForeColor = Color.FromArgb(185, 185, 195);
        Controls.Add(body);

        CheckBox remember = new CheckBox();
        remember.Text = "Don't ask again for approved restarts";
        remember.Location = new Point(18, 198);
        remember.Size = new Size(340, 24);
        remember.ForeColor = Color.FromArgb(200, 200, 208);
        Controls.Add(remember);

        Button cancelBtn = MakeButton("Cancel", new Point(18, 240), 90, false);
        cancelBtn.Click += delegate
        {
            Choice = "cancel";
            Close();
        };
        Controls.Add(cancelBtn);

        bool canMoveOnly = plan.Kind != "undo" && plan.Kind != "restart" && plan.Kind != "recover";
        if (canMoveOnly)
        {
            Button moveOnlyBtn = MakeButton("Move Ready Only", new Point(168, 240), 132, false);
            moveOnlyBtn.Click += delegate
            {
                Choice = "move-only";
                Close();
            };
            Controls.Add(moveOnlyBtn);
        }

        string primaryLabel = plan.Kind == "undo" ? "Restart & Undo" : (plan.Kind == "restart" ? "Restart Claude" : "Restart & Move");
        Button approveBtn = MakeButton(primaryLabel, new Point(310, 240), 132, true);
        approveBtn.Click += delegate
        {
            Choice = "approve";
            RememberChoice = remember.Checked;
            Close();
        };
        Controls.Add(approveBtn);
        AcceptButton = approveBtn;
        CancelButton = cancelBtn;
    }

    private static Button MakeButton(string text, Point location, int width, bool primary)
    {
        Button b = new Button();
        b.Text = text;
        b.Location = location;
        b.Size = new Size(width, 32);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = primary ? Color.FromArgb(10, 132, 255) : Color.FromArgb(72, 72, 80);
        b.BackColor = primary ? Color.FromArgb(10, 132, 255) : Color.FromArgb(48, 48, 54);
        b.ForeColor = Color.White;
        return b;
    }
}

internal sealed class TrayPopupForm : Form
{
    private readonly TrayModel model;
    private readonly List<HitRegion> regions;
    private readonly Action onQuit;
    private string hoveredKey;
    public bool SuppressDeactivate;

    public TrayPopupForm(TrayModel trayModel, Action quitAction)
    {
        model = trayModel;
        onQuit = quitAction;
        regions = new List<HitRegion>();
        hoveredKey = null;
        SuppressDeactivate = false;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(28, 28, 30);
        KeyPreview = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;
            cp.ExStyle |= 0x00000080;
            return cp;
        }
    }

    public float DpiScale
    {
        get
        {
            using (Graphics g = CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }
    }

    public void ResizeAndPositionNearTray()
    {
        float scale = DpiScale;
        SizeF logical = PanelRenderer.MeasurePanel(model);
        int w = Math.Max(200, (int)Math.Ceiling(logical.Width * scale));
        int h = Math.Max(120, (int)Math.Ceiling(logical.Height * scale));
        Point cursor = Cursor.Position;
        Rectangle work = Screen.FromPoint(cursor).WorkingArea;

        int x = cursor.X - w / 2;
        if (x + w > work.Right - 8) x = work.Right - w - 8;
        if (x < work.Left + 8) x = work.Left + 8;

        int y = cursor.Y > (work.Top + work.Height / 2) ? (work.Bottom - h - 8) : (work.Top + 8);
        if (y < work.Top + 8) y = work.Top + 8;

        Bounds = new Rectangle(x, y, w, h);
        Invalidate();
    }

    public void UpdateSizeIfVisible()
    {
        if (!Visible) return;
        float scale = DpiScale;
        SizeF logical = PanelRenderer.MeasurePanel(model);
        int w = Math.Max(200, (int)Math.Ceiling(logical.Width * scale));
        int h = Math.Max(120, (int)Math.Ceiling(logical.Height * scale));
        if (Width != w || Height != h)
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int newY = Top;
            if (Bottom >= work.Bottom - 16) newY = Math.Max(work.Top + 8, work.Bottom - h - 8);
            Bounds = new Rectangle(Left, newY, w, h);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DpiScale;
        SizeF logical = new SizeF(Width / scale, Height / scale);
        using (Bitmap rendered = PanelRenderer.RenderBitmap(model, scale, logical, hoveredKey, regions))
        {
            e.Graphics.DrawImage(rendered, 0, 0, Width, Height);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        float scale = DpiScale;
        PointF pt = new PointF(e.X / scale, e.Y / scale);
        string found = null;
        for (int i = regions.Count - 1; i >= 0; i--)
        {
            if (regions[i].Bounds.Contains(pt))
            {
                found = regions[i].Kind + ":" + regions[i].Value;
                break;
            }
        }
        if (found != hoveredKey)
        {
            hoveredKey = found;
            Cursor = found != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoveredKey != null)
        {
            hoveredKey = null;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        float scale = DpiScale;
        PointF pt = new PointF(e.X / scale, e.Y / scale);
        for (int i = regions.Count - 1; i >= 0; i--)
        {
            HitRegion hit = regions[i];
            if (!hit.Bounds.Contains(pt)) continue;
            DispatchHit(hit);
            return;
        }
    }

    private void DispatchHit(HitRegion hit)
    {
        if (hit.Kind == "source") model.ToggleSource(hit.Value);
        else if (hit.Kind == "target") model.SelectTarget(hit.Value);
        else if (hit.Kind == "move") model.StartMove();
        else if (hit.Kind == "finish") model.StartFinish(null, false);
        else if (hit.Kind == "undo") model.StartUndo(null);
        else if (hit.Kind == "keep-local") model.StartKeepLocal(hit.Value);
        else if (hit.Kind == "restart") model.StartRestart(null);
        else if (hit.Kind == "details") { model.DetailsExpanded = !model.DetailsExpanded; model.NotifyChanged(); }
        else if (hit.Kind == "update") model.InstallUpdate();
        else if (hit.Kind == "quit")
        {
            Hide();
            if (onQuit != null) onQuit();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!SuppressDeactivate) Hide();
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon notifyIcon;
    private readonly TrayModel model;
    private readonly TrayPopupForm popup;
    private readonly System.Windows.Forms.Timer pollTimer;
    private readonly System.Windows.Forms.Timer updateTimer;
    private Icon currentIcon;

    public TrayApplicationContext(TrayConfig config)
    {
        SynchronizationContext sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(sync);

        model = new TrayModel(config, sync);
        popup = new TrayPopupForm(model, ExitCleanly);

        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem openItem = new ToolStripMenuItem("Open Claude Transplant");
        openItem.Click += delegate { TogglePopup(); };
        ToolStripMenuItem restartItem = new ToolStripMenuItem("Restart Claude Desktop");
        restartItem.Click += delegate { model.StartRestart(null); };
        ToolStripMenuItem quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += delegate { ExitCleanly(); };
        menu.Items.Add(openItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        currentIcon = TrayIconFactory.Create(model.Symbol, model.Badge);
        notifyIcon = new NotifyIcon();
        notifyIcon.Icon = currentIcon;
        notifyIcon.Text = TruncateTooltip(model.Tooltip);
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.Visible = true;
        notifyIcon.MouseClick += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        };

        model.OnChanged = delegate
        {
            UpdateTrayVisuals();
            popup.UpdateSizeIfVisible();
        };
        model.OnNotify = delegate(string title, string text, ToolTipIcon icon)
        {
            try { notifyIcon.ShowBalloonTip(4000, title, text, icon); } catch { }
        };
        model.OnPromptRestart = delegate(RestartPlanData plan)
        {
            popup.SuppressDeactivate = true;
            try
            {
                using (RestartDialogForm dlg = new RestartDialogForm(plan))
                {
                    dlg.ShowDialog();
                    if (dlg.Choice == "approve" && dlg.RememberChoice)
                    {
                        model.Prefs.AutoApproveRestart = true;
                        model.Prefs.Save(model.Config);
                    }
                    return dlg.Choice;
                }
            }
            finally
            {
                popup.SuppressDeactivate = false;
            }
        };

        pollTimer = new System.Windows.Forms.Timer();
        pollTimer.Interval = 15000;
        pollTimer.Tick += delegate { model.RefreshAsync(true); };
        pollTimer.Start();

        updateTimer = new System.Windows.Forms.Timer();
        updateTimer.Interval = 6 * 3600 * 1000;
        updateTimer.Tick += delegate { model.CheckForUpdatesAsync(); };
        updateTimer.Start();

        model.RefreshAsync(true);
        model.CheckForUpdatesAsync();
    }

    private void TogglePopup()
    {
        if (popup.Visible)
        {
            popup.Hide();
            return;
        }
        model.RefreshAsync(false);
        popup.ResizeAndPositionNearTray();
        popup.Show();
        popup.Activate();
    }

    private void UpdateTrayVisuals()
    {
        string sym = model.RecoveryProblem == null ? model.Symbol : "exclamationmark.triangle";
        string bdg = model.RecoveryProblem == null ? model.Badge : "";
        Icon next = TrayIconFactory.Create(sym, bdg);
        Icon old = currentIcon;
        currentIcon = next;
        notifyIcon.Icon = currentIcon;
        notifyIcon.Text = TruncateTooltip(model.Tooltip);
        if (old != null) old.Dispose();
    }

    private static string TruncateTooltip(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Claude Transplant";
        return s.Length <= 63 ? s : s.Substring(0, 63);
    }

    private void ExitCleanly()
    {
        pollTimer.Stop();
        updateTimer.Stop();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        if (currentIcon != null) currentIcon.Dispose();
        popup.Dispose();
        ExitThread();
    }
}

internal static class CliHelpers
{
    public static int RunLock(string lockPath)
    {
        if (string.IsNullOrEmpty(lockPath)) return 73;
        FileStream stream = null;
        try
        {
            string dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            int code = Marshal.GetHRForException(ex) & 0xFFFF;
            return (code == 32 || code == 33) ? 75 : 73;
        }
        catch
        {
            return 73;
        }
        try
        {
            Console.Out.Write("ready");
            Console.Out.Flush();
            char[] buffer = new char[256];
            while (Console.In.Read(buffer, 0, buffer.Length) > 0) { }
            return 0;
        }
        finally
        {
            stream.Dispose();
        }
    }

    public static int RunProcessSnapshot()
    {
        try
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CreationDate, Name, ExecutablePath, CommandLine FROM Win32_Process"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    int pid = item["ProcessId"] != null ? Convert.ToInt32(item["ProcessId"], CultureInfo.InvariantCulture) : 0;
                    int ppid = item["ParentProcessId"] != null ? Convert.ToInt32(item["ParentProcessId"], CultureInfo.InvariantCulture) : 0;
                    string name = item["Name"] != null ? Convert.ToString(item["Name"], CultureInfo.InvariantCulture) : "";
                    string exe = item["ExecutablePath"] != null ? Convert.ToString(item["ExecutablePath"], CultureInfo.InvariantCulture) : "";
                    string cmd = item["CommandLine"] != null ? Convert.ToString(item["CommandLine"], CultureInfo.InvariantCulture) : "";
                    string started = "";
                    string rawDate = item["CreationDate"] != null ? Convert.ToString(item["CreationDate"], CultureInfo.InvariantCulture) : null;
                    if (!string.IsNullOrEmpty(rawDate))
                    {
                        try
                        {
                            started = ManagementDateTimeConverter.ToDateTime(rawDate).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                        }
                        catch { }
                    }
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    row["ProcessId"] = pid;
                    row["ParentProcessId"] = ppid;
                    row["Started"] = started;
                    row["Name"] = name;
                    row["ExecutablePath"] = exe;
                    row["CommandLine"] = cmd;
                    rows.Add(row);
                }
            }
            Console.Out.WriteLine(JsonUtil.CreateSerializer().Serialize(rows));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static int RunGracefulQuit(string pidArg)
    {
        int pid;
        if (!int.TryParse(pidArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid <= 0) return 1;
        Process proc = null;
        try
        {
            proc = Process.GetProcessById(pid);
            if (proc.HasExited) return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
        catch
        {
            return 1;
        }

        using (proc)
        {
            List<IntPtr> visibleWindows = new List<IntPtr>();
            List<IntPtr> allWindows = new List<IntPtr>();
            NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint winPid;
                NativeMethods.GetWindowThreadProcessId(hWnd, out winPid);
                if ((int)winPid == pid)
                {
                    allWindows.Add(hWnd);
                    if (NativeMethods.IsWindowVisible(hWnd)) visibleWindows.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr hWnd in visibleWindows)
            {
                IntPtr res;
                NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, 3000, out res);
            }
            if (visibleWindows.Count > 0 && proc.WaitForExit(1200)) return 0;

            allWindows.Clear();
            NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint winPid;
                NativeMethods.GetWindowThreadProcessId(hWnd, out winPid);
                if ((int)winPid == pid) allWindows.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            if (proc.HasExited) return 0;
            if (allWindows.Count == 0) return 1;

            IntPtr closeAppFlag = new IntPtr(1);
            foreach (IntPtr hWnd in allWindows)
            {
                IntPtr queryRes;
                IntPtr ok = NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_QUERYENDSESSION, IntPtr.Zero, closeAppFlag, NativeMethods.SMTO_ABORTIFHUNG, 5000, out queryRes);
                if (ok == IntPtr.Zero || queryRes == IntPtr.Zero) return 1;
            }
            foreach (IntPtr hWnd in allWindows)
            {
                IntPtr endRes;
                NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_ENDSESSION, new IntPtr(1), closeAppFlag, NativeMethods.SMTO_ABORTIFHUNG, 5000, out endRes);
            }
            proc.WaitForExit(2000);
            return 0;
        }
    }

    public static int RunOpenDesktop(string target)
    {
        if (string.IsNullOrEmpty(target)) return 1;
        try
        {
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = target;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                return 0;
            }
            ProcessStartInfo exeInfo = new ProcessStartInfo();
            exeInfo.FileName = target;
            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) exeInfo.WorkingDirectory = dir;
            exeInfo.UseShellExecute = true;
            Process.Start(exeInfo);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static int RunSnapshot(string outputFile, string explicitNode, string explicitScript)
    {
        try
        {
            TrayConfig cfg = TrayConfig.Discover(explicitNode, explicitScript);
            TrayModel model = new TrayModel(cfg, null);
            if (!model.LoadAccountsSync() || model.SnapshotFailed) return 1;
            string dir = Path.GetDirectoryName(Path.GetFullPath(outputFile));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            List<HitRegion> regions = new List<HitRegion>();
            using (Bitmap bmp = PanelRenderer.RenderBitmap(model, 2.0f, null, null, regions))
            {
                bmp.Save(outputFile, ImageFormat.Png);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static int RunDemo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            List<AccountItem> demoAccounts = new List<AccountItem>();
            demoAccounts.Add(MakeDemo("work", "acme", "you@work.com", "Acme Inc.", true, true, nowMs - 2 * 3600000.0, "161 | 2h ago | acme-api | active"));
            demoAccounts.Add(MakeDemo("work", "work-personal", "you@work.com", "Personal", false, true, nowMs - 86400000.0, "157 | 1d ago | acme-api"));
            demoAccounts.Add(MakeDemo("home", "home-personal", "you@home.com", "Personal", false, false, nowMs - 5 * 86400000.0, "3 | 5d ago | notes"));
            demoAccounts.Add(MakeDemo("work2", "northwind", "you@work2.com", "Northwind", false, false, nowMs - 4 * 60000.0, "12 | 4m ago | northwind"));

            TrayModel model = new TrayModel(null, null);
            model.ApplyAccounts(demoAccounts);
            model.SelectTarget(demoAccounts[2].Id);
            model.Lines.Add(new LogLine("inventory", "318 records | 3 already there | 307 to move"));
            model.Lines.Add(new LogLine("move", "307 ✓ | 307 zero-copy"));
            model.Lines.Add(new LogLine("verify", "chat messages unchanged ✓ | sidecars unchanged ✓ | desktop ✓"));
            model.Note = "307 sessions moved";
            model.Completion = new CompletionBanner(model.Note, "History verified");
            SizeF fixedSize = PanelRenderer.MeasurePanel(model);

            model.Lines.Clear();
            model.Note = "";
            model.Completion = null;
            model.RestartAvailable = false;

            List<int> durations = new List<int>();
            SaveDemoFrame(model, fixedSize, directory, durations, 1200);
            model.ToggleSource(demoAccounts[3].Id);
            SaveDemoFrame(model, fixedSize, directory, durations, 1200);
            model.ToggleSource(demoAccounts[0].Id);
            SaveDemoFrame(model, fixedSize, directory, durations, 900);
            model.ToggleSource(demoAccounts[0].Id);
            SaveDemoFrame(model, fixedSize, directory, durations, 600);

            model.BeginRun();
            model.MoveProgress = new MoveProgressState(0);
            model.MoveProgress.Update("scan", 0, 318, 0);
            model.MoveProgress.SetSessions(307, 307);
            model.Lines.Add(new LogLine("inventory", "318 records | 3 already there | 307 to move"));
            SaveDemoFrame(model, fixedSize, directory, durations, 300);

            model.MoveProgress.Update("scan", 318, 318, 2);
            int[] steps = new int[] { 75, 170, 250, 307 };
            for (int i = 0; i < steps.Length; i++)
            {
                int done = steps[i];
                model.MoveProgress.Update("move", done, 307, 3 + i);
                model.MoveProgress.SetSessions(307, 307 - done);
                SaveDemoFrame(model, fixedSize, directory, durations, 180);
            }

            model.Running = false;
            model.MoveProgress = null;
            model.Badge = "";
            model.Lines.Add(new LogLine("move", "307 ✓ | 307 zero-copy"));
            model.Lines.Add(new LogLine("verify", "chat messages unchanged ✓ | sidecars unchanged ✓ | desktop ✓"));
            model.Symbol = "checkmark";
            model.HandleEventLine("{\"done\":true,\"ok\":true,\"complete\":true,\"moved\":307,\"receipt\":\"demo\",\"failed\":[],\"waiting\":[]}");
            SaveDemoFrame(model, fixedSize, directory, durations, 2400);

            File.WriteAllText(Path.Combine(directory, "durations.json"), JsonUtil.CreateSerializer().Serialize(durations), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void SaveDemoFrame(TrayModel model, SizeF size, string directory, List<int> durations, int ms)
    {
        List<HitRegion> regions = new List<HitRegion>();
        string frameName = string.Format(CultureInfo.InvariantCulture, "frame-{0:D3}.png", durations.Count);
        using (Bitmap bmp = PanelRenderer.RenderBitmap(model, 2.0f, size, null, regions))
        {
            bmp.Save(Path.Combine(directory, frameName), ImageFormat.Png);
        }
        durations.Add(ms);
    }

    private static AccountItem MakeDemo(string account, string org, string email, string orgName, bool active, bool signedIn, double activeAt, string stats)
    {
        AccountItem a = new AccountItem();
        a.Account = account;
        a.Org = org;
        a.Email = email;
        a.OrgName = orgName;
        a.Label = email + " · " + orgName;
        a.Active = active;
        a.SignedIn = signedIn;
        a.ActiveAt = activeAt;
        a.Stats = stats;
        a.IdentityState = "known";
        return a;
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args != null && args.Length > 0)
        {
            string mode = args[0];
            if (mode == "--lock") return args.Length > 1 ? CliHelpers.RunLock(args[1]) : 73;
            if (mode == "--ps") return CliHelpers.RunProcessSnapshot();
            if (mode == "--quit") return args.Length > 1 ? CliHelpers.RunGracefulQuit(args[1]) : 1;
            if (mode == "--open") return args.Length > 1 ? CliHelpers.RunOpenDesktop(args[1]) : 1;
            if (mode == "--demo") return args.Length > 1 ? CliHelpers.RunDemo(args[1]) : 1;
            if (mode == "--snapshot")
            {
                if (args.Length < 2) return 1;
                string node = FindFlagValue(args, "--node");
                string script = FindFlagValue(args, "--script");
                return CliHelpers.RunSnapshot(args[1], node, script);
            }
        }

        bool createdNew;
        using (Mutex singleInstance = new Mutex(true, @"Local\ClaudeTransplantTrayMutex", out createdNew))
        {
            if (!createdNew) return 0;
            try { NativeMethods.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string explicitNode = FindFlagValue(args, "--node");
            string explicitScript = FindFlagValue(args, "--script");
            TrayConfig config = TrayConfig.Discover(explicitNode, explicitScript);
            Application.Run(new TrayApplicationContext(config));
            return 0;
        }
    }

    private static string FindFlagValue(string[] args, string flag)
    {
        if (args == null) return null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }
}
