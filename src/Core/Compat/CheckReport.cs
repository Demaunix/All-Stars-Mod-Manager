using System.Text.Json.Serialization;

namespace AllStarsModManager.Compat;

public enum CheckVerdict { DeclaredCompatible, InsufficientInformation, Incompatible }

public static class CheckExit
{
    public const int DeclaredCompatible = 0, InsufficientInformation = 1, Incompatible = 2, InputError = 3, Usage = 64;
    public static int For(CheckVerdict v) => v switch { CheckVerdict.DeclaredCompatible => 0, CheckVerdict.InsufficientInformation => 1, _ => 2 };
    public static string Name(CheckVerdict v) => v switch
    {
        CheckVerdict.DeclaredCompatible => "declared-compatible",
        CheckVerdict.InsufficientInformation => "insufficient-information",
        _ => "incompatible"
    };
}

/// <summary>One thing the check found. "blocker" is definite; "unknown" means it could not be confirmed; "note" never changes the result.</summary>
public sealed class CheckFinding
{
    [JsonPropertyName("impact")] public string Impact { get; set; } = "note";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    /// <summary>"loader": the game's own loader performs the same check and would refuse the whole mod set. "tools": a rule only this manager and checker apply (declared by the mod or static analysis).</summary>
    [JsonPropertyName("enforced_by")] public string EnforcedBy { get; set; } = "tools";
    [JsonPropertyName("packages")] public List<string> Packages { get; set; } = new();
}

public sealed class CheckedPackage
{
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    /// <summary>active (the loader starts it), would-be-added (--add), switched-off, unmanaged</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("description_file")] public string? DescriptionFile { get; set; }
    [JsonPropertyName("runtime")] public string? Runtime { get; set; }
    [JsonPropertyName("homepage")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Homepage { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public sealed class CheckReport
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "allstars-check-report/1";
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("exit_code")] public int ExitCode { get; set; }
    [JsonPropertyName("input_kind")] public string InputKind { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("executed_mod_code")] public bool ExecutedModCode => false;
    [JsonPropertyName("modified_anything")] public bool ModifiedAnything => false;
    [JsonPropertyName("file_hashes")] public string FileHashes { get; set; } = "";
    [JsonPropertyName("loader")] public CheckLoader Loader { get; set; } = new();
    [JsonPropertyName("start_order")] public List<string> StartOrder { get; set; } = new();
    [JsonPropertyName("packages")] public List<CheckedPackage> Packages { get; set; } = new();
    [JsonPropertyName("findings")] public List<CheckFinding> Findings { get; set; } = new();
    [JsonPropertyName("not_checked")] public List<string> NotChecked { get; set; } = new();

    [JsonIgnore] public CheckVerdict VerdictValue { get; set; }
    [JsonIgnore] public int Blockers => Findings.Count(f => f.Impact == "blocker");
    [JsonIgnore] public int Unknowns => Findings.Count(f => f.Impact == "unknown");
}

public sealed class CheckLoader
{
    /// <summary>verified, assumed, unrecognized, absent or not-inspected</summary>
    [JsonPropertyName("knowledge")] public string Knowledge { get; set; } = "not-inspected";
    [JsonPropertyName("profile")] public string? Profile { get; set; }
    /// <summary>built-in (the table shipped with the program) or user-supplied (a profile file you gave; your own statement, not evidence)</summary>
    [JsonPropertyName("profile_source")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProfileSource { get; set; }
    [JsonPropertyName("host_sha256")] public string? HostSha256 { get; set; }
    [JsonPropertyName("proxy_sha256")] public string? ProxySha256 { get; set; }
    [JsonPropertyName("game_executable_sha256")] public string? GameExecutableSha256 { get; set; }
}
