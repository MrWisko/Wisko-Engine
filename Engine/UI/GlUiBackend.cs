using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>OpenGL implementation of the UI seam: the existing ScreenUiRenderer / ImageQuadRenderer.</summary>
internal sealed class GlUiBackend(FontLibrary fonts) : IUiBackend
{
    private static readonly GlTextureFactory Textures = new();

    public IUiRenderer CreateRenderer() => CreateRenderer(fonts);

    public IUiRenderer CreateRenderer(FontLibrary fonts)
    {
        var renderer = new ScreenUiRenderer();
        renderer.Initialize(fonts);
        return renderer;
    }

    public FontLibrary CreateFontLibrary()
    {
        var library = new FontLibrary();
        library.Initialize();
        return library;
    }

    public IUiImageRenderer CreateImageRenderer()
    {
        var images = new ImageQuadRenderer();
        images.Initialize();
        return images;
    }

    public IUiTextureCache CreateTextureCache() => new TextureFileCache(Textures);

    public UiTexture CreateTexture(byte[] rgba, int width, int height) => Textures.Create(rgba, width, height);

    public void DestroyTexture(UiTexture texture) => Textures.Destroy(texture);

    private sealed class GlTextureFactory : IUiTextureFactory
    {
        public UiTexture Create(byte[] rgba, int width, int height) => new(ImageQuadRenderer.CreateTexture(rgba, width, height));

        public void Destroy(UiTexture texture) => ImageQuadRenderer.DeleteTexture(texture.Id);
    }
}
