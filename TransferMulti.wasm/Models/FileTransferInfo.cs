namespace TransferMulti.wasm.Models;

internal class FileTransferInfo : FileMetadata
{
    public byte[]? FileContext { get; set; }
    public long ReceivedBytes { get; set; }
    public FileTransferStateEnum State { get; set; }
    public double TransferProgress { get; set; }
    public double UploadProgress { get; set; }
    public bool Succeed { get; set; }
    public string Message { get; set; } = "";
    public object LockObject { get; } = new();
}

internal class FileMetadata
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = null!;
    public string SHA1 { get; set; } = null!;
    public long FileSize { get; set; }
}
