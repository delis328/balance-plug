using System;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;
using BalancePlug.Core;

namespace BalancePlug;

public static class MascotImages
{
    public static BitmapImage Decode(byte[] bytes, int maximumPixels = 900)
    {
        if (bytes.Length > 10 * 1024 * 1024) throw new BalanceException("图片不得超过 10 MB");
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            throw new BalanceException("请选择有效的 PNG 图片");
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) > 4096 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) > 4096)
            throw new BalanceException("图片宽高不得超过 4096 像素");
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096) throw new BalanceException("图片宽高不得超过 4096 像素");
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(Math.Max(1, maximumPixels), frame.PixelWidth);
            else image.DecodePixelHeight = Math.Min(Math.Max(1, maximumPixels), frame.PixelHeight);
            image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or ArgumentException or InvalidOperationException)
        { throw new BalanceException("PNG 图片损坏，已保留原形象"); }
    }
}
