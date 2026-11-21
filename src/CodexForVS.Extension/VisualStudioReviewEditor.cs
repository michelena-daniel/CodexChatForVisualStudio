using System;
using System.IO;
using System.Linq;

using CodexForVS.Client;
using CodexForVS.Core;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodexForVS.Extension;

internal sealed class VisualStudioReviewEditor : IReviewEditor
{
    private static DTE Automation { get { ThreadHelper.ThrowIfNotOnUIThread(); return (DTE)Package.GetGlobalService(typeof(DTE)); } }
    public void Compare(FileProposal proposal, FileComparison comparison)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        IVsDifferenceService difference = (IVsDifferenceService)Package.GetGlobalService(typeof(SVsDifferenceService));
        difference.OpenComparisonWindow2(comparison.Left, comparison.Right, "Codex review: {0} and {1}",
            null, "Committed file", "Proposed file", null, null, (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_DetectBinaryFiles);
    }
    public void Edit(FileProposal proposal)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Automation.ItemOperations.OpenFile(proposal.Right, EnvDTE.Constants.vsViewKindTextView);
    }
    public void CheckBuffers(WorktreeSession session, FileProposal proposal)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string[] targets = new[] { proposal.Before, proposal.After }.OfType<string>()
            .Select(path => Path.GetFullPath(Path.Combine(session.RepositoryRoot, path))).ToArray();
        IVsRunningDocumentTable4 table = (IVsRunningDocumentTable4)Package.GetGlobalService(typeof(SVsRunningDocumentTable));
        foreach (string path in targets.Concat(new[] { proposal.Right }))
        {
            if (table.IsMonikerValid(path) && table.IsDocumentDirty(table.GetDocumentCookie(path)))
                throw new IOException("Save or resolve the unsaved editor buffer before accepting: " + path);
        }
        foreach (Document document in Automation.Documents)
        {
            string path = document.FullName;
            if (targets.Contains(path, StringComparer.OrdinalIgnoreCase))
                throw new IOException("Close the active solution's editor for this file before accepting: " + path);
            if (string.Equals(path, proposal.Right, StringComparison.OrdinalIgnoreCase) && !document.Saved)
                throw new IOException("Save your proposal edits and reopen the diff before accepting.");
        }
    }
}
