using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

/// <summary>
/// Free commands in the original runtime ignore handles that are not live: bbFreeImage is
/// <c>if( !image_set.erase(i) ) return;</c>, bbFreeTexture / bbFreeBrush are <c>if( !t ) return;</c> plus a set erase,
/// bbFreeFont ends in <c>gxGraphics::freeFont: if( font_set.erase( f ) ) delete f;</c>, and the debug-only
/// <c>debugTexture / debugBrush / debugFont</c> checks are skipped in release builds (<c>if( debug )</c>). SCP:CB relies
/// on that when it toggles "Antialiased text" (Menu.bb frees <c>font\texture</c> and <c>font\backup</c> on records that
/// never had them). These tests run without a renderer by registering renderer-free texture state.
/// </summary>
[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsFreeCommandTests
{
    private const int Unknown = 987_654_321;

    [Fact]
    public void FreeTexture_ignores_zero_unknown_and_already_freed_handles()
    {
        WithCleanState(() =>
        {
            var kept = new LegacyGraphics.TextureRep(910_101, 4, 4, 1, []);
            var freed = new LegacyGraphics.TextureRep(910_102, 4, 4, 1, []);
            LegacyGraphics.RegisterTextureForTesting(kept);
            LegacyGraphics.RegisterTextureForTesting(freed);

            LegacyGraphics.FreeTexture(0);
            LegacyGraphics.FreeTexture(Unknown);
            LegacyGraphics.FreeTexture(-1);
            Assert.True(LegacyGraphics.TextureRegisteredForTesting(kept.Handle));
            Assert.True(LegacyGraphics.TextureRegisteredForTesting(freed.Handle));

            LegacyGraphics.FreeTexture(freed.Handle);
            Assert.False(LegacyGraphics.TextureRegisteredForTesting(freed.Handle));
            Assert.True(freed.Disposed);
            Assert.Equal(0, freed.CallerHandles);

            LegacyGraphics.FreeTexture(freed.Handle);

            Assert.False(LegacyGraphics.TextureRegisteredForTesting(freed.Handle));
            Assert.Equal(0, freed.CallerHandles);
            Assert.True(LegacyGraphics.TextureRegisteredForTesting(kept.Handle));
            Assert.False(kept.Disposed);
            Assert.Equal(1, kept.CallerHandles);
        });
    }

    [Fact]
    public void FreeTexture_double_free_of_one_alias_does_not_release_the_other_alias()
    {
        WithCleanState(() =>
        {
            var rep = new LegacyGraphics.TextureRep(910_111, 4, 4, 1, []);
            LegacyGraphics.RegisterTextureForTesting(rep);
            LegacyGraphics.RegisterTextureAliasForTesting(910_112, rep);
            Assert.Equal(2, rep.CallerHandles);

            LegacyGraphics.FreeTexture(910_111);
            LegacyGraphics.FreeTexture(910_111);
            LegacyGraphics.FreeTexture(910_111);

            Assert.Equal(1, rep.CallerHandles);
            Assert.False(rep.Disposed);
            Assert.True(LegacyGraphics.TextureRegisteredForTesting(910_112));

            LegacyGraphics.FreeTexture(910_112);
            LegacyGraphics.FreeTexture(910_112);

            Assert.Equal(0, rep.CallerHandles);
            Assert.True(rep.Disposed);
        });
    }

    [Fact]
    public void FreeTexture_keeps_the_texture_alive_for_a_brush_binding_and_a_second_free_changes_nothing()
    {
        WithCleanState(() =>
        {
            var rep = new LegacyGraphics.TextureRep(910_121, 4, 4, 1, []);
            LegacyGraphics.RegisterTextureForTesting(rep);
            rep.BindingReferences++;
            var binding = new LegacyGraphics.TextureBinding(rep, new LegacyGraphics.TextureFrame(null!, 910_122));

            LegacyGraphics.FreeTexture(rep.Handle);
            LegacyGraphics.FreeTexture(rep.Handle);

            // The brush still owns the canvas: only the caller handle went away (original: refcounted Texture rep).
            Assert.False(LegacyGraphics.TextureRegisteredForTesting(rep.Handle));
            Assert.False(rep.Disposed);
            Assert.Equal(0, rep.CallerHandles);
            Assert.Equal(1, rep.BindingReferences);

            LegacyGraphics.ReleaseTextureBinding(binding);
            Assert.True(rep.Disposed);
            Assert.Equal(0, rep.BindingReferences);

            // Releasing the binding a second time must not over-release or throw.
            LegacyGraphics.ReleaseTextureBinding(binding);
            Assert.Equal(0, rep.BindingReferences);
        });
    }

    [Fact]
    public void FreeTexture_does_not_touch_the_decoded_pixel_cache()
    {
        WithCleanState(() =>
        {
            var pixels = new byte[16];
            const string path = "/tmp/free-command-tests/never-read.png";
            TextureDecodeCache.Add(path, 1, 16, false, 2, 2, pixels);
            var rep = new LegacyGraphics.TextureRep(910_131, 2, 2, 1, []);
            LegacyGraphics.RegisterTextureForTesting(rep);

            LegacyGraphics.FreeTexture(rep.Handle);
            LegacyGraphics.FreeTexture(rep.Handle);

            Assert.True(TextureDecodeCache.TryGet(path, 1, 16, false, out var width, out var height, out var cached));
            Assert.Equal((2, 2), (width, height));
            Assert.Same(pixels, cached);
        });
    }

    [Fact]
    public void FreeImage_ignores_zero_unknown_and_already_freed_handles()
    {
        WithCleanState(() =>
        {
            var keptTexture = new LegacyGraphics.TextureRep(910_201, 4, 4, 2, []);
            var freedTexture = new LegacyGraphics.TextureRep(910_202, 4, 4, 2, []);
            LegacyGraphics.RegisterTextureForTesting(keptTexture);
            LegacyGraphics.RegisterTextureForTesting(freedTexture);
            var kept = LegacyGraphics.RegisterImageForTesting(keptTexture.Handle, 4, 4, 1);
            var freed = LegacyGraphics.RegisterImageForTesting(freedTexture.Handle, 4, 4, 1);

            LegacyGraphics.FreeImage(0);
            LegacyGraphics.FreeImage(Unknown);
            LegacyGraphics.FreeImage(-1);
            Assert.True(LegacyGraphics.ImageRegisteredForTesting(kept));
            Assert.True(LegacyGraphics.ImageRegisteredForTesting(freed));

            LegacyGraphics.FreeImage(freed);
            Assert.False(LegacyGraphics.ImageRegisteredForTesting(freed));
            Assert.False(LegacyGraphics.TextureRegisteredForTesting(freedTexture.Handle));
            Assert.True(freedTexture.Disposed);

            LegacyGraphics.FreeImage(freed);

            Assert.False(LegacyGraphics.ImageRegisteredForTesting(freed));
            Assert.True(LegacyGraphics.ImageRegisteredForTesting(kept));
            Assert.True(LegacyGraphics.TextureRegisteredForTesting(keptTexture.Handle));
            Assert.False(keptTexture.Disposed);
            Assert.Equal(1, keptTexture.CallerHandles);
        });
    }

    [Fact]
    public void FreeImage_tolerates_an_image_whose_texture_is_already_gone()
    {
        WithCleanState(() =>
        {
            var texture = new LegacyGraphics.TextureRep(910_211, 4, 4, 2, []);
            LegacyGraphics.RegisterTextureForTesting(texture);
            var image = LegacyGraphics.RegisterImageForTesting(texture.Handle, 4, 4, 1);

            LegacyGraphics.FreeTexture(texture.Handle);
            LegacyGraphics.FreeImage(image);
            LegacyGraphics.FreeImage(image);

            Assert.False(LegacyGraphics.ImageRegisteredForTesting(image));
            Assert.Equal(0, texture.CallerHandles);
        });
    }

    [Fact]
    public void FreeImage_zero_does_not_close_an_open_movie_that_has_no_decode_target()
    {
        WithCleanState(() =>
        {
            var factory = LegacyGraphics.MovieVideoFactory;
            var video = new StillVideo();
            try
            {
                LegacyGraphics.MovieVideoFactory = _ => video;
                Assert.Equal(1, LegacyGraphics.BlitzMovie_Open("fixture.avi"));

                // A movie opened without a target image has Image == 0; an unmatched FreeImage(0) must leave it playing.
                LegacyGraphics.FreeImage(0);
                LegacyGraphics.FreeImage(Unknown);

                Assert.False(video.Disposed);
                Assert.Equal(2, LegacyGraphics.BlitzMovie_GetWidth());
            }
            finally
            {
                LegacyGraphics.BlitzMovie_Close();
                LegacyGraphics.MovieVideoFactory = factory;
            }
        });
    }

    [Fact]
    public void FreeFont_ignores_zero_unknown_and_negative_handles()
    {
        WithCleanState(() =>
        {
            // Original release build: freeFont does `if( font_set.erase( f ) ) delete f;`, so nothing happens.
            LegacyGraphics.FreeFont(0);
            LegacyGraphics.FreeFont(Unknown);
            LegacyGraphics.FreeFont(-1);
            LegacyGraphics.FreeFont(Unknown);
        });
    }

    [Fact]
    public void FreeBrush_ignores_zero_unknown_and_already_freed_handles()
    {
        WithCleanState(() =>
        {
            var kept = LegacyGraphics.CreateBrush(10, 20, 30);
            var freed = LegacyGraphics.CreateBrush(40, 50, 60);

            LegacyGraphics.FreeBrush(0);
            LegacyGraphics.FreeBrush(Unknown);
            LegacyGraphics.FreeBrush(freed);
            LegacyGraphics.FreeBrush(freed);

            Assert.Throws<ArgumentException>(() => LegacyGraphics.GetBrushSettingsForTesting(freed));
            Assert.Equal((0, 0, 0), LegacyGraphics.GetBrushSettingsForTesting(kept));
        });
    }

    [Fact]
    public void FreeEntity_zero_is_a_no_op_but_a_double_free_still_reports_the_missing_entity()
    {
        WithCleanState(() =>
        {
            var survivor = LegacyScene.CreatePivot();
            var freed = LegacyScene.CreatePivot();

            LegacyGraphics.FreeEntity(0);
            LegacyGraphics.FreeEntity(freed);

            // bbFreeEntity is `if( !e ) return; if( debug ){ debugEntity(e); erase(e); } delete e;`: only 0 is defined.
            // A dangling entity is "Entity does not exist" under the debug flag and undefined behaviour in a release
            // build, so there is no lenient behaviour to copy and the adapter keeps failing loudly.
            Assert.Throws<KeyNotFoundException>(() => LegacyGraphics.FreeEntity(freed));
            Assert.Equal(string.Empty, LegacyScene.EntityName(survivor));
        });
    }

    /// <summary>
    /// Replays Menu.bb's "Antialiased text" OFF cleanup over three font records the way AAText.bb creates them:
    /// <c>For font.AAFont = Each AAFont : FreeFont font\lowResFont : If (Not AATextEnable) : FreeTexture font\texture :
    /// FreeImage font\backup : EndIf : Delete font : Next</c>. Record A has an atlas texture and a backup image, record B
    /// is the scale-factor-1 ConsoleFont (texture and backup stay 0), and record C holds handles that were already freed.
    /// </summary>
    [Fact]
    public void Antialiased_text_toggle_off_cleanup_sequence_does_not_throw_for_any_record_shape()
    {
        WithCleanState(() =>
        {
            var atlas = new LegacyGraphics.TextureRep(910_301, 1024, 1024, 3, []);
            var backupTexture = new LegacyGraphics.TextureRep(910_302, 1024, 1024, 2, []);
            var staleTexture = new LegacyGraphics.TextureRep(910_303, 1024, 1024, 3, []);
            var staleBackupTexture = new LegacyGraphics.TextureRep(910_304, 1024, 1024, 2, []);
            foreach (var rep in new[] { atlas, backupTexture, staleTexture, staleBackupTexture })
                LegacyGraphics.RegisterTextureForTesting(rep);
            var backup = LegacyGraphics.RegisterImageForTesting(backupTexture.Handle, 1024, 1024, 1);
            var staleBackup = LegacyGraphics.RegisterImageForTesting(staleBackupTexture.Handle, 1024, 1024, 1);
            LegacyGraphics.FreeTexture(staleTexture.Handle);
            LegacyGraphics.FreeImage(staleBackup);

            (int LowResFont, int Texture, int Backup)[] records =
            [
                (Unknown, atlas.Handle, backup),                       // AA record (the font handle is a real one on Windows)
                (0, 0, 0),                                             // ConsoleFont: LoadFont failed or AATextScaleFactor = 1
                (Unknown + 1, staleTexture.Handle, staleBackup),       // handles that were freed earlier
            ];

            foreach (var record in records)
            {
                LegacyGraphics.FreeFont(record.LowResFont);
                LegacyGraphics.FreeTexture(record.Texture);
                LegacyGraphics.FreeImage(record.Backup);
            }
            // Toggling OFF again re-walks the same stale handles (a second Each-loop, or Delete not reached).
            foreach (var record in records)
            {
                LegacyGraphics.FreeFont(record.LowResFont);
                LegacyGraphics.FreeTexture(record.Texture);
                LegacyGraphics.FreeImage(record.Backup);
            }

            Assert.False(LegacyGraphics.TextureRegisteredForTesting(atlas.Handle));
            Assert.False(LegacyGraphics.TextureRegisteredForTesting(backupTexture.Handle));
            Assert.False(LegacyGraphics.ImageRegisteredForTesting(backup));
            Assert.True(atlas.Disposed);
            Assert.True(backupTexture.Disposed);
            Assert.Equal(0, atlas.CallerHandles);
            Assert.Equal(0, backupTexture.CallerHandles);
        });
    }

    private static void WithCleanState(Action action)
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try { action(); }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    private sealed class StillVideo : IMovieVideo
    {
        public int Width => 2;
        public int Height => 1;
        public long Duration100ns => 100;
        public byte[] RgbaFrame { get; } = new byte[8];
        public long FrameTimestamp100ns => 0;
        public bool Disposed { get; private set; }
        public bool DecodeAt(long targetTime100ns) => false;
        public void SeekToStart() { }
        public void Dispose() => Disposed = true;
    }
}
