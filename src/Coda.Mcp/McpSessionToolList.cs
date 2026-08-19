using System.Collections;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// A live view of a session's MCP tool set: the currently connected servers' tools followed by a
/// fixed set of helper tools. Needed because <c>SessionOptions.ExtraTools</c> is captured once at
/// session construction but re-enumerated on every turn — a plain snapshot would keep handing the
/// model wrappers bound to a client that has since been disposed (and its process killed), which is
/// exactly what happens after <see cref="RestartMcpServerTool"/> replaces a server.
/// <para>
/// The projection is rebuilt only when <see cref="McpClientManager.Version"/> changes, so per-turn
/// enumeration stays cheap and a single build sees one consistent list.
/// </para>
/// </summary>
public sealed class McpSessionToolList : IReadOnlyList<ITool>
{
    private readonly McpClientManager manager;
    private readonly IReadOnlyList<ITool> helpers;
    private readonly object gate = new();
    private IReadOnlyList<ITool> cached = [];
    private int cachedVersion = -1;

    public McpSessionToolList(McpClientManager manager, IReadOnlyList<ITool> helpers)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.helpers = helpers ?? throw new ArgumentNullException(nameof(helpers));
    }

    public int Count => this.Snapshot().Count;

    public ITool this[int index] => this.Snapshot()[index];

    public IEnumerator<ITool> GetEnumerator() => this.Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    private IReadOnlyList<ITool> Snapshot()
    {
        lock (this.gate)
        {
            var version = this.manager.Version;
            if (version != this.cachedVersion)
            {
                this.cached = [.. this.manager.Tools, .. this.helpers];
                this.cachedVersion = version;
            }

            return this.cached;
        }
    }
}
