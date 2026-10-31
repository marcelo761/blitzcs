using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyMaterialTests
{
    [Fact]
    public void Entity_material_values_are_copied_but_later_changes_are_independent()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var original = LegacyGraphics.CreateCube();
            LegacyGraphics.EntityColor(original, 127.5f, 255, 63.75f);
            LegacyGraphics.EntityAlpha(original, 0.5f);
            var copy = LegacyGraphics.CopyEntity(original);
            Assert.Equal(new Vector4(0.5f, 1, 0.25f, 0.5f), LegacyGraphics.GetEntityMaterialColorForTesting(copy));
            LegacyGraphics.EntityColor(original, 0, 0, 0);
            LegacyGraphics.EntityAlpha(original, 1);
            Assert.Equal(new Vector4(0.5f, 1, 0.25f, 0.5f), LegacyGraphics.GetEntityMaterialColorForTesting(copy));
            LegacyGraphics.FreeEntity(original);
            Assert.Equal(0.5f, LegacyGraphics.GetEntityMaterialColorForTesting(copy).W);
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.EntityAlpha(copy, float.NaN));
            Assert.Equal(0.5f, LegacyGraphics.GetEntityMaterialColorForTesting(copy).W);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }
}
