namespace Blitz3DCS.Engine;

public sealed partial class SceneGraph
{
    /// <summary>Copies enabled entities in root-first hierarchy order into reusable caller storage.</summary>
    public void CopyVisibleEntityIds(List<int> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach (var node in _nodes.Values)
            if (node.Parent is null) Visit(node);

        void Visit(Node node)
        {
            if (!node.Enabled) return;
            destination.Add(node.Id);
            foreach (var child in node.Children) Visit(child);
        }
    }
}
