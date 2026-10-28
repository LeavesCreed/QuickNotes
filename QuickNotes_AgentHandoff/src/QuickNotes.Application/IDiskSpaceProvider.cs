namespace QuickNotes.Application;

public interface IDiskSpaceProvider
{
    long GetAvailableBytes(string path);
}
