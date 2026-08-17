// Monaco Editor JavaScript Interop for WebSSMS
window.WebSSMS = window.WebSSMS || {};

window.WebSSMS.Monaco = {
    editors: {},

    initialize: async function (elementId, initialValue, language, dotnetRef) {
        // Load Monaco from CDN if not already loaded
        if (!window.monaco) {
            await this._loadMonaco();
        }

        const container = document.getElementById(elementId);
        if (!container) return;

        const editor = monaco.editor.create(container, {
            value: initialValue || '',
            language: language || 'sql',
            theme: 'vs-dark',
            automaticLayout: true,
            minimap: { enabled: true },
            fontSize: 14,
            fontFamily: "'Cascadia Code', 'Consolas', 'Courier New', monospace",
            lineNumbers: 'on',
            renderLineHighlight: 'line',
            scrollBeyondLastLine: false,
            wordWrap: 'off',
            tabSize: 4,
            insertSpaces: true,
            formatOnPaste: true,
            suggestOnTriggerCharacters: true,
            quickSuggestions: true,
            snippetSuggestions: 'inline',
            scrollbar: {
                verticalScrollbarSize: 10,
                horizontalScrollbarSize: 10
            },
            padding: { top: 8 },
            folding: true,
            bracketPairColorization: { enabled: true },
            contextmenu: true,
            find: {
                addExtraSpaceOnTop: false,
                autoFindInSelection: 'never',
                seedSearchStringFromSelection: 'always'
            }
        });

        // Store reference
        this.editors[elementId] = { editor, dotnetRef };

        // Register change handler
        editor.onDidChangeModelContent(() => {
            const value = editor.getValue();
            dotnetRef.invokeMethodAsync('OnContentChanged', value);
        });

        // Register keyboard shortcuts
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => {
            dotnetRef.invokeMethodAsync('OnExecuteRequested');
        });

        editor.addCommand(monaco.KeyCode.F5, () => {
            dotnetRef.invokeMethodAsync('OnExecuteRequested');
        });

        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyE, () => {
            const selection = editor.getSelection();
            const selectedText = editor.getModel().getValueInRange(selection);
            dotnetRef.invokeMethodAsync('OnExecuteSelectionRequested', selectedText || editor.getValue());
        });

        // Parse shortcut (Ctrl+F5)
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.F5, () => {
            dotnetRef.invokeMethodAsync('OnParseRequested');
        });

        return true;
    },

    getValue: function (elementId) {
        const entry = this.editors[elementId];
        return entry ? entry.editor.getValue() : '';
    },

    setValue: function (elementId, value) {
        const entry = this.editors[elementId];
        if (entry) {
            entry.editor.setValue(value || '');
        }
    },

    getSelectedText: function (elementId) {
        const entry = this.editors[elementId];
        if (!entry) return '';
        const selection = entry.editor.getSelection();
        return entry.editor.getModel().getValueInRange(selection);
    },

    focus: function (elementId) {
        const entry = this.editors[elementId];
        if (entry) entry.editor.focus();
    },

    setLanguage: function (elementId, language) {
        const entry = this.editors[elementId];
        if (entry) {
            monaco.editor.setModelLanguage(entry.editor.getModel(), language);
        }
    },

    setReadOnly: function (elementId, readOnly) {
        const entry = this.editors[elementId];
        if (entry) {
            entry.editor.updateOptions({ readOnly });
        }
    },

    registerCompletionProvider: function (suggestions) {
        if (!window.monaco) return;

        monaco.languages.registerCompletionItemProvider('sql', {
            provideCompletionItems: function (model, position) {
                const word = model.getWordUntilPosition(position);
                const range = {
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: word.startColumn,
                    endColumn: word.endColumn
                };

                return {
                    suggestions: suggestions.map(s => ({
                        label: s.label,
                        kind: monaco.languages.CompletionItemKind[s.kind] || monaco.languages.CompletionItemKind.Text,
                        documentation: s.documentation || '',
                        insertText: s.insertText || s.label,
                        range: range,
                        detail: s.detail || ''
                    }))
                };
            }
        });
    },

    addMarkers: function (elementId, markers) {
        const entry = this.editors[elementId];
        if (!entry) return;

        const model = entry.editor.getModel();
        const monacoMarkers = markers.map(m => ({
            severity: m.severity === 'error'
                ? monaco.MarkerSeverity.Error
                : m.severity === 'warning'
                    ? monaco.MarkerSeverity.Warning
                    : monaco.MarkerSeverity.Info,
            message: m.message,
            startLineNumber: m.startLine,
            startColumn: m.startColumn,
            endLineNumber: m.endLine || m.startLine,
            endColumn: m.endColumn || m.startColumn + 1
        }));

        monaco.editor.setModelMarkers(model, 'webssms', monacoMarkers);
    },

    clearMarkers: function (elementId) {
        const entry = this.editors[elementId];
        if (!entry) return;
        monaco.editor.setModelMarkers(entry.editor.getModel(), 'webssms', []);
    },

    dispose: function (elementId) {
        const entry = this.editors[elementId];
        if (entry) {
            entry.editor.dispose();
            delete this.editors[elementId];
        }
    },

    _loadMonaco: function () {
        return new Promise((resolve, reject) => {
            if (window.monaco) { resolve(); return; }

            const script = document.createElement('script');
            script.src = 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs/loader.min.js';
            script.onload = () => {
                require.config({
                    paths: { 'vs': 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs' }
                });
                require(['vs/editor/editor.main'], () => {
                    // Configure SQL language defaults
                    this._configureSqlLanguage();
                    resolve();
                });
            };
            script.onerror = reject;
            document.head.appendChild(script);
        });
    },

    _configureSqlLanguage: function () {
        // Register SQL keywords for better highlighting
        const sqlKeywords = [
            'SELECT', 'FROM', 'WHERE', 'INSERT', 'UPDATE', 'DELETE', 'CREATE', 'ALTER', 'DROP',
            'TABLE', 'VIEW', 'PROCEDURE', 'FUNCTION', 'TRIGGER', 'INDEX', 'DATABASE',
            'INTO', 'VALUES', 'SET', 'JOIN', 'LEFT', 'RIGHT', 'INNER', 'OUTER', 'CROSS',
            'ON', 'AND', 'OR', 'NOT', 'IN', 'EXISTS', 'BETWEEN', 'LIKE', 'IS', 'NULL',
            'AS', 'ORDER', 'BY', 'GROUP', 'HAVING', 'UNION', 'ALL', 'DISTINCT', 'TOP',
            'WITH', 'NOLOCK', 'BEGIN', 'END', 'IF', 'ELSE', 'WHILE', 'RETURN', 'EXEC',
            'EXECUTE', 'DECLARE', 'CURSOR', 'FETCH', 'OPEN', 'CLOSE', 'DEALLOCATE',
            'TRY', 'CATCH', 'THROW', 'RAISERROR', 'PRINT', 'GO', 'USE', 'GRANT', 'DENY',
            'REVOKE', 'BACKUP', 'RESTORE', 'TRANSACTION', 'COMMIT', 'ROLLBACK', 'SAVE',
            'PRIMARY', 'KEY', 'FOREIGN', 'REFERENCES', 'CONSTRAINT', 'UNIQUE', 'CHECK',
            'DEFAULT', 'IDENTITY', 'CLUSTERED', 'NONCLUSTERED', 'ASC', 'DESC',
            'COUNT', 'SUM', 'AVG', 'MIN', 'MAX', 'CASE', 'WHEN', 'THEN', 'ELSE', 'END',
            'CAST', 'CONVERT', 'ISNULL', 'COALESCE', 'NULLIF', 'MERGE', 'OUTPUT',
            'OVER', 'PARTITION', 'ROW_NUMBER', 'RANK', 'DENSE_RANK', 'NTILE',
            'SCHEMA', 'AUTHORIZATION', 'TRUNCATE', 'BULK', 'OPENROWSET'
        ];

        // Register built-in completions
        this.registerCompletionProvider(
            sqlKeywords.map(kw => ({
                label: kw,
                kind: 'Keyword',
                insertText: kw,
                detail: 'SQL Keyword'
            }))
        );
    }
};
