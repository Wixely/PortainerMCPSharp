namespace PortainerMCPSharp.Configuration;

/// <summary>
/// Domain options for the Portainer MCP server. Points the server at one or more Portainer
/// instances, sets the layered safety gates that decide what the agent may change, and caps
/// the payload sizes that flow back through the MCP channel.
///
/// The gates are deliberately layered rather than a single switch: this server can stop
/// containers and delete stacks and volumes across an entire estate, so
/// <see cref="ReadOnly"/>=false on its own must never unlock the destructive tail.
/// </summary>
public sealed class PortainerOptions
{
    public const string SectionName = "Portainer";

    // ---------------------------------------------------------------- instances

    /// <summary>Configured Portainer instances. Each is addressed by its alias.</summary>
    public List<PortainerInstanceEntry> Instances { get; set; } = new();

    /// <summary>
    /// Alias picked when a tool omits <c>alias</c>. Falls back to the first entry in
    /// <see cref="Instances"/> when blank.
    /// </summary>
    public string? DefaultAlias { get; set; }

    // ---------------------------------------------------------------- master gate

    /// <summary>
    /// Master safety switch. When true, every mutating tool refuses with a clear error naming
    /// the config key. Inspection tools stay available. Default true — flip it per environment.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    // ---------------------------------------------------------------- per-category gates
    // Each of these additionally requires ReadOnly=false. Defaults follow blast radius:
    // routine operations default on, anything that destroys state defaults off.

    /// <summary>Create / update / redeploy stacks, set stack env vars, change git config.</summary>
    public bool AllowStackDeploy { get; set; } = true;

    /// <summary>Start and stop stacks.</summary>
    public bool AllowStackLifecycle { get; set; } = true;

    /// <summary>Delete a stack. Off by default — this removes its containers.</summary>
    public bool AllowStackDelete { get; set; } = false;

    /// <summary>Start / stop / restart / kill / rename containers.</summary>
    public bool AllowContainerLifecycle { get; set; } = true;

    /// <summary>Create containers directly (outside a stack). Off by default.</summary>
    public bool AllowContainerCreate { get; set; } = false;

    /// <summary>Remove containers. Off by default.</summary>
    public bool AllowContainerRemoval { get; set; } = false;

    /// <summary>Pull images.</summary>
    public bool AllowImagePull { get; set; } = true;

    /// <summary>Remove images. Off by default.</summary>
    public bool AllowImageRemoval { get; set; } = false;

    /// <summary>
    /// Create / remove volumes, and permit <c>removeVolumes=true</c> when deleting a stack.
    /// Off by default: volume removal destroys data that nothing else can restore.
    /// </summary>
    public bool AllowVolumeManagement { get; set; } = false;

    /// <summary>Create / remove networks and connect / disconnect containers. Off by default.</summary>
    public bool AllowNetworkManagement { get; set; } = false;

    /// <summary>Every <c>*_prune_*</c> tool. Off by default — prunes are broad and irreversible.</summary>
    public bool AllowPrune { get; set; } = false;

    /// <summary>Swarm service update/scale and node availability changes. Off by default.</summary>
    public bool AllowSwarmMutation { get; set; } = false;

    /// <summary>Trigger a stack's redeploy webhook.</summary>
    public bool AllowWebhookTrigger { get; set; } = true;

    // ---------------------------------------------------------------- scoping

    /// <summary>Optional allow-list of Portainer environment (endpoint) ids. Empty = no restriction.</summary>
    public List<int> AllowedEnvironmentIds { get; set; } = new();

    /// <summary>Optional deny-list of environment ids. Evaluated after <see cref="AllowedEnvironmentIds"/>.</summary>
    public List<int> BlockedEnvironmentIds { get; set; } = new();

    /// <summary>Optional allow-list of stack names (case-insensitive). Empty = no restriction.</summary>
    public List<string> AllowedStacks { get; set; } = new();

    /// <summary>Optional deny-list of stack names. Evaluated after <see cref="AllowedStacks"/>.</summary>
    public List<string> BlockedStacks { get; set; } = new();

    /// <summary>Container names that must never be mutated, whatever the gates say.</summary>
    public List<string> BlockedContainers { get; set; } = new();

    /// <summary>
    /// Refuse any mutating operation aimed at Portainer's own container or agent, matched on
    /// image name (<c>portainer/portainer-*</c>, <c>portainer/agent</c>) and container name.
    ///
    /// An agent that stops the Portainer container severs the connection it is using and cannot
    /// undo it without access to the host, so this defaults on. Turn it off only if you have
    /// another route back in.
    /// </summary>
    public bool ProtectPortainerItself { get; set; } = true;

    // ---------------------------------------------------------------- output shaping

    /// <summary>
    /// Mask values of environment variables whose key looks like a secret. The key stays
    /// visible so an agent can still see which variables exist and set them.
    ///
    /// Default true because Portainer returns stack and container environments in cleartext,
    /// and those values would otherwise flow into the agent's context, its logs, and its
    /// transcript. Set false deliberately when an agent genuinely has to migrate config.
    /// </summary>
    public bool RedactSecrets { get; set; } = true;

    /// <summary>
    /// Regex matched (case-insensitively) against an environment variable's key to decide
    /// whether its value is redacted. Only used when <see cref="RedactSecrets"/> is true.
    /// </summary>
    public string SecretKeyPattern { get; set; } = "PASSWORD|PASSWD|SECRET|TOKEN|APIKEY|API_KEY|CREDENTIAL|PRIVATE_KEY|_KEY$|^KEY$";

    // ---------------------------------------------------------------- feature toggles

    /// <summary>Expose stack tools.</summary>
    public bool EnableStacks { get; set; } = true;

    /// <summary>Expose environment (endpoint) tools.</summary>
    public bool EnableEnvironments { get; set; } = true;

    /// <summary>Expose Docker container tools.</summary>
    public bool EnableContainers { get; set; } = true;

    /// <summary>Expose Docker image tools.</summary>
    public bool EnableImages { get; set; } = true;

    /// <summary>Expose Docker volume tools.</summary>
    public bool EnableVolumes { get; set; } = true;

    /// <summary>Expose Docker network tools.</summary>
    public bool EnableNetworks { get; set; } = true;

    /// <summary>Expose Swarm service / node / task tools.</summary>
    public bool EnableSwarm { get; set; } = true;

    /// <summary>Expose registry tools.</summary>
    public bool EnableRegistries { get; set; } = true;

    /// <summary>Expose Portainer system tools.</summary>
    public bool EnableSystem { get; set; } = true;

    // ---------------------------------------------------------------- defaults and caps

    /// <summary>
    /// Environment id used when a tool omits <c>environmentId</c> and it cannot be inferred
    /// from the target resource. Null means the caller must supply one.
    /// </summary>
    public int? DefaultEnvironmentId { get; set; }

    /// <summary>Cap on items returned by any single list tool.</summary>
    public int MaxItems { get; set; } = 1000;

    /// <summary>Cap on bytes of container or service log returned in one call.</summary>
    public int MaxLogBytes { get; set; } = 200_000;

    /// <summary>Cap on characters of any single JSON blob echoed back (inspect payloads).</summary>
    public int MaxJsonChars { get; set; } = 60_000;

    /// <summary>Cap on characters of a stack's compose file returned by the file tool.</summary>
    public int MaxStackFileChars { get; set; } = 200_000;

    /// <summary>Default seconds a deploy tool waits for a stack to settle when asked to wait.</summary>
    public int DefaultWaitSeconds { get; set; } = 60;

    /// <summary>Hard ceiling on the wait a tool call may request, whatever the caller asks for.</summary>
    public int MaxWaitSeconds { get; set; } = 600;

    /// <summary>Poll interval (ms) used while waiting for a stack to settle.</summary>
    public int PollIntervalMs { get; set; } = 1500;

    /// <summary>HTTP request timeout in seconds for calls to Portainer.</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>User-Agent header sent to Portainer.</summary>
    public string UserAgent { get; set; } = "PortainerMCPSharp";
}

/// <summary>One configured Portainer instance.</summary>
public sealed class PortainerInstanceEntry
{
    /// <summary>
    /// Stable handle the agent uses to pick this instance. When omitted the registry generates
    /// one at load time (<c>portainer-1</c>, <c>portainer-2</c>, …).
    /// </summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>Base URL of the Portainer instance, e.g. <c>https://portainer.example.com/</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Portainer API token (<c>ptr_…</c>), sent as the <c>X-API-KEY</c> header. Preferred over
    /// username/password: long-lived, revocable, and it cannot expire mid-run.
    ///
    /// Note it is NOT a bearer token. Putting a Portainer API token in an
    /// <c>Authorization: Bearer</c> header returns 401 despite the credential being valid;
    /// this server picks the header from which credential is configured so that cannot happen.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Username, used only when <see cref="ApiKey"/> is blank. Exchanged for a JWT.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password for <see cref="Username"/>. Supports a <c>file:&lt;path&gt;</c> prefix.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Free-text description shown by <c>portainer_list_instances</c>.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>If true, skip TLS verification. For Portainer's default self-signed certificate.</summary>
    public bool AllowInvalidCertificate { get; set; } = false;

    /// <summary>
    /// Which credential this entry will use, for display. Reports "none" rather than guessing
    /// when neither is set, so an unconfigured instance is obvious in the startup banner and on
    /// /healthz instead of only surfacing on the first tool call.
    /// </summary>
    public string CredentialKind =>
        !string.IsNullOrWhiteSpace(ApiKey) ? "ApiKey"
        : !string.IsNullOrWhiteSpace(Username) ? "Password"
        : "none";
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5716;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "PortainerMCPSharp";

    /// <summary>Optional MCP endpoint password. Blank disables MCP password auth.</summary>
    public string Password { get; set; } = string.Empty;
}
