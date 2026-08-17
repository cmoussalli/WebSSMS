// Context Menu positioning and management
window.WebSSMS = window.WebSSMS || {};

window.WebSSMS.ContextMenu = {
    show: function (menuId, x, y) {
        const menu = document.getElementById(menuId);
        if (!menu) return;

        menu.style.display = 'block';

        // Ensure menu stays within viewport
        const rect = menu.getBoundingClientRect();
        const viewportWidth = window.innerWidth;
        const viewportHeight = window.innerHeight;

        if (x + rect.width > viewportWidth) {
            x = viewportWidth - rect.width - 8;
        }
        if (y + rect.height > viewportHeight) {
            y = viewportHeight - rect.height - 8;
        }

        menu.style.left = x + 'px';
        menu.style.top = y + 'px';
    },

    hide: function (menuId) {
        const menu = document.getElementById(menuId);
        if (menu) {
            menu.style.display = 'none';
        }
    },

    hideAll: function () {
        document.querySelectorAll('.context-menu').forEach(menu => {
            menu.style.display = 'none';
        });
    },

    init: function () {
        // Close context menus on click outside
        document.addEventListener('click', (e) => {
            if (!e.target.closest('.context-menu')) {
                this.hideAll();
            }
        });

        // Close context menus on escape
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                this.hideAll();
            }
        });

        // Close context menus on scroll
        document.addEventListener('scroll', () => {
            this.hideAll();
        }, true);
    }
};

// Initialize on load
document.addEventListener('DOMContentLoaded', () => {
    window.WebSSMS.ContextMenu.init();
});
