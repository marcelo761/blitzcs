namespace Blitz3DCS.Engine;

public sealed partial class SceneGraph
{
    /// <summary>Copies enabled entities in root-first hierarchy order into reusable caller storage.</summary>
    public void CopyVisibleEntityIds(List<int> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach (var node in _nodes.Values)
            if (node.Parent is null) Visit(node, destination);

        static void Visit(Node node, List<int> destination)
        {
            if (!node.Enabled) return;
            destination.Add(node.Id);
            foreach (var child in node.Children) Visit(child, destination);
        }
    }

    /// <summary>
    /// Same sequence as <see cref="CopyVisibleEntityIds"/> (the maintained root list is slot-ordered, i.e. the
    /// dictionary's iteration order) but also yields each node's world transform, without the root scan over every
    /// node and without a per-entity id lookup.
    /// </summary>
    public void CopyVisibleEntityWorlds(List<int> ids, List<SceneTransform> worlds)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(worlds);
        ids.Clear();
        worlds.Clear();
        for (var i = 0; i < _roots.Count; i++) VisitWorld(_roots[i], ids, worlds);

        static void VisitWorld(Node node, List<int> ids, List<SceneTransform> worlds)
        {
            if (!node.Enabled) return;
            ids.Add(node.Id);
            worlds.Add(GetWorldTransform(node));
            var children = node.Children;
            for (var i = 0; i < children.Count; i++) VisitWorld(children[i], ids, worlds);
        }
    }
}
