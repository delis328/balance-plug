using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class TestAssets
{
    public static byte[] Png(int width, int height, byte color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(100+color*20); pixels[i+1] = 160; pixels[i+2] = 210; pixels[i+3] = 230; }
        var image = BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
