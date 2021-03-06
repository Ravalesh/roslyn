// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Undo;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation
{
    internal partial class GlobalUndoServiceFactory
    {
        private class WorkspaceUndoTransaction : ForegroundThreadAffinitizedObject, IWorkspaceGlobalUndoTransaction
        {
            private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
            private readonly IVsLinkedUndoTransactionManager _undoManager;
            private readonly Workspace _workspace;
            private readonly string _description;
            private readonly GlobalUndoService _service;

            // indicate whether undo transaction is currently active
            private bool _transactionAlive;

            public WorkspaceUndoTransaction(
                ITextUndoHistoryRegistry undoHistoryRegistry,
                IVsLinkedUndoTransactionManager undoManager,
                Workspace workspace,
                string description,
                GlobalUndoService service)
                : base(assertIsForeground: true)
            {
                _undoHistoryRegistry = undoHistoryRegistry;
                _undoManager = undoManager;
                _workspace = workspace;
                _description = description;
                _service = service;

                Marshal.ThrowExceptionForHR(_undoManager.OpenLinkedUndo((uint)LinkedTransactionFlags2.mdtGlobal, _description));
                _transactionAlive = true;
            }

            public void AddDocument(DocumentId id)
            {
                var vsWorkspace = (VisualStudioWorkspaceImpl)_workspace;
                Contract.ThrowIfNull(vsWorkspace);

                var solution = vsWorkspace.CurrentSolution;
                if (!solution.ContainsDocument(id))
                {
                    // document is not part of the workspace (newly created document that is not applied to the workspace yet?)
                    return;
                }

                if (vsWorkspace.IsDocumentOpen(id))
                {
                    var document = vsWorkspace.GetHostDocument(id);
                    var undoHistory = _undoHistoryRegistry.RegisterHistory(document.GetOpenTextBuffer());

                    using (var undoTransaction = undoHistory.CreateTransaction(_description))
                    {
                        undoTransaction.AddUndo(new NoOpUndoPrimitive());
                        undoTransaction.Complete();
                    }

                    return;
                }

                // open and close the document so that it is included in the global undo transaction
                using (vsWorkspace.OpenInvisibleEditor(id))
                {
                    // empty
                }
            }

            public void Commit()
            {
                AssertIsForeground();

                // once either commit or disposed is called, don't do finalizer check
                GC.SuppressFinalize(this);

                if (_transactionAlive)
                {
                    _service.ActiveTransactions--;

                    var result = _undoManager.CloseLinkedUndo();
                    if (result == VSConstants.UNDO_E_CLIENTABORT)
                    {
                        Dispose();
                    }
                    else
                    {
                        Marshal.ThrowExceptionForHR(result);
                        _transactionAlive = false;
                    }
                }
            }

            public void Dispose()
            {
                AssertIsForeground();

                // once either commit or disposed is called, don't do finalizer check
                GC.SuppressFinalize(this);

                if (_transactionAlive)
                {
                    _service.ActiveTransactions--;

                    Marshal.ThrowExceptionForHR(_undoManager.AbortLinkedUndo());
                    _transactionAlive = false;
                }
            }

            ~WorkspaceUndoTransaction()
            {
                // make sure we closed it correctly
                Contract.Requires(!_transactionAlive);
            }
        }
    }
}
