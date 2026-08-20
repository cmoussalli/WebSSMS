// AI assistant panel interop for WebSSMS.
//
// The LLM settings themselves live on the server now (Services/AiSettingsStore.cs).
// What is left here is reading the copy an older build kept in this browser, so it
// can be adopted once, plus the panel's own scrolling and resizing.
window.WebSSMS = window.WebSSMS || {};

window.WebSSMS.Ai = {
    storageKey: 'webssms.ai.settings',

    // Settings an older build left in localStorage. Read once, then cleared.
    loadSettings: function () {
        try {
            return window.localStorage.getItem(this.storageKey);
        } catch (e) {
            // Private mode, or storage disabled by policy.
            return null;
        }
    },

    clearSettings: function () {
        try {
            window.localStorage.removeItem(this.storageKey);
        } catch (e) {
            // Nothing to clear.
        }
    },

    scrollToBottom: function (elementId) {
        const element = document.getElementById(elementId);
        if (element) element.scrollTop = element.scrollHeight;
    },

    focus: function (elementId) {
        const element = document.getElementById(elementId);
        if (element) element.focus();
    },

    copyToClipboard: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            return false;
        }
    },

    // The panel sits at the right edge, so dragging its splitter right has to
    // shrink it -- the mirror image of SplitPanel.initVertical.
    initResizer: function (splitterId, panelId, minWidth, maxWidth) {
        const splitter = document.getElementById(splitterId);
        const panel = document.getElementById(panelId);
        if (!splitter || !panel || splitter.dataset.aiResizer === 'on') return;

        splitter.dataset.aiResizer = 'on';

        let startX, startWidth;

        const onMouseMove = (e) => {
            const width = startWidth - (e.clientX - startX);
            const ceiling = Math.min(maxWidth || 900, window.innerWidth - 300);
            if (width >= (minWidth || 280) && width <= ceiling) {
                panel.style.width = width + 'px';
            }
        };

        const onMouseUp = () => {
            splitter.classList.remove('active');
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        };

        splitter.addEventListener('mousedown', (e) => {
            e.preventDefault();
            startX = e.clientX;
            startWidth = panel.getBoundingClientRect().width;
            splitter.classList.add('active');
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        });
    }
};
