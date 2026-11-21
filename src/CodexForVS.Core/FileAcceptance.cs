using System;
using System.IO;
using System.Threading;
using System.Xml.Linq;

namespace CodexForVS.Core;

internal static class FileAcceptance
{
    internal static void Apply(FileProposal proposal, string root, string directory, string? before, string? after, string? expected, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        string? backup = before == null ? null : Path.Combine(Path.GetDirectoryName(before)!, ".codex-" + id + ".bak");
        string? temp = after == null ? null : Path.Combine(Path.GetDirectoryName(after)!, ".codex-" + id + ".tmp");
        string journal = Path.Combine(directory, "accept-" + id + ".xml");
        bool installed = false;
        bool displaced = false;
        bool ownsTemp = false;
        XElement intent = new XElement("acceptance", new XAttribute("state", "preparing"), new XElement("before", before),
            new XElement("after", after), new XElement("backup", backup), new XElement("temporary", temp),
            new XElement("originalSha256", expected), new XElement("proposedSha256", proposal.PreviewDigest));
        using (FileStream record = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { intent.Save(record); record.Flush(true); }

        using PublicationDirectories directories = new PublicationDirectories(root);
        directories.PinParent(before,false);
        directories.PinParent(after,true);
        try
        {
            if (after != null)
            {

                ReviewPaths.Copy(proposal.Right, temp!);
                ownsTemp = true;
                if (ReviewPaths.Digest(temp!) != proposal.PreviewDigest) throw new IOException("The proposal changed during acceptance.");
            }
            token.ThrowIfCancellationRequested();
            if (before != null && ReviewPaths.Digest(before) != expected) throw new IOException("Concurrent change to " + before);
            if (before != null && before == after)
            {
                File.Replace(temp!, before, backup);
                installed = displaced = true;
            }
            else
            {
                // Install an addition first using a no-overwrite move. A rename source stays
                // intact if the destination exists or another writer creates it during preflight.
                if (after != null) { File.Move(temp!, after); installed = true; }
                if (before != null) { File.Move(before, backup!); displaced = true; }
            }
            if (displaced && ReviewPaths.Digest(backup!) != expected) throw new IOException("A writer changed the original file during acceptance.");
        }
        catch (Exception error)
        {
            try
            {
                if (installed && after != null)
                {
                    // Save a concurrent writer's bytes even if it races rollback.
                    string retained = after + "." + id + ".rollback";
                    if (displaced && before == after) { File.Replace(backup!, after, retained); displaced = false; }
                    else File.Move(after, retained);
                    if (ReviewPaths.Digest(retained) == proposal.PreviewDigest) File.Delete(retained);
                    else throw new IOException("Concurrent content retained at " + retained);
                }
                if (displaced) File.Move(backup!, before!);
            }
            catch (Exception rollback)
            {
                throw new IOException("Incomplete rollback. See " + journal + ". " + rollback.Message, error);
            }
            throw;
        }
        finally { if (ownsTemp && !installed && File.Exists(temp)) File.Delete(temp); }

        try
        {
            intent.SetAttributeValue("state", "accepted");
            intent.Save(journal + ".done");
            File.Replace(journal + ".done", journal, null);
            if (displaced) File.Delete(backup!);
        }
        catch (Exception error) { throw new IOException("File accepted. Recovery metadata or backup cleanup needs attention at " + journal, error); }
    }
}
