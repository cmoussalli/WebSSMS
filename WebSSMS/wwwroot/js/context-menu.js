// Context Menu positioning and dismissal.
//
// Visibility is owned entirely by the Blazor component. This file never hides a
// menu by touching the DOM -- doing so would leave the component thinking it is
// still open, and the next render would put it straight back on screen. Instead
// the dismissal triggers below call back into .NET so the component can drop its
// own state.
window.WebSSMS = window.WebSSMS || {};

window.WebSSMS.ContextMenu = {
    _dismissHandlers: new Map(),

    // Nudge an already-rendered menu so it stays inside the viewport.
    position: function (menuId, x, y) {
        const menu = document.getElementById(menuId);
        if (!menu) return;

        const rect = menu.getBoundingClientRect();

        if (x + rect.width > window.innerWidth) {
            x = Math.max(0, window.innerWidth - rect.width - 8);
        }
        if (y + rect.height > window.innerHeight) {
            y = Math.max(0, window.innerHeight - rect.height - 8);
        }

        menu.style.left = x + 'px';
        menu.style.top = y + 'px';
    },

    // Wire up click-outside / Escape / scroll for one open menu.
    registerDismiss: function (menuId, dotNetRef) {
        this.unregisterDismiss(menuId);

        const close = () => dotNetRef.invokeMethodAsync('CloseFromJs');

        // A left click inside the menu is the item click itself -- let Blazor handle it.
        const onClick = (e) => {
            if (!e.target.closest('.context-menu')) close();
        };
        const onKeyDown = (e) => {
            if (e.key === 'Escape') close();
        };
        const onScroll = () => close();

        document.addEventListener('click', onClick);
        document.addEventListener('keydown', onKeyDown);
        document.addEventListener('scroll', onScroll, true);

        this._dismissHandlers.set(menuId, { onClick, onKeyDown, onScroll });
    },

    unregisterDismiss: function (menuId) {
        const handlers = this._dismissHandlers.get(menuId);
        if (!handlers) return;

        document.removeEventListener('click', handlers.onClick);
        document.removeEventListener('keydown', handlers.onKeyDown);
        document.removeEventListener('scroll', handlers.onScroll, true);

        this._dismissHandlers.delete(menuId);
    }
};
