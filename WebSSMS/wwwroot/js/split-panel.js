// Resizable Split Panel JavaScript Interop
window.WebSSMS = window.WebSSMS || {};

window.WebSSMS.SplitPanel = {
    activeSplitter: null,

    initHorizontal: function (splitterId, topPanelId, bottomPanelId, initialTopPercent) {
        const splitter = document.getElementById(splitterId);
        const topPanel = document.getElementById(topPanelId);
        const bottomPanel = document.getElementById(bottomPanelId);
        if (!splitter || !topPanel || !bottomPanel) return;

        const container = splitter.parentElement;
        let startY, startTopHeight;

        topPanel.style.height = initialTopPercent + '%';
        bottomPanel.style.flex = '1';

        const onMouseDown = (e) => {
            e.preventDefault();
            startY = e.clientY;
            startTopHeight = topPanel.getBoundingClientRect().height;
            splitter.classList.add('active');
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            document.body.style.cursor = 'row-resize';
            document.body.style.userSelect = 'none';
        };

        const onMouseMove = (e) => {
            const delta = e.clientY - startY;
            const containerHeight = container.getBoundingClientRect().height;
            const newHeight = startTopHeight + delta;
            const percent = (newHeight / containerHeight) * 100;
            if (percent > 10 && percent < 90) {
                topPanel.style.height = percent + '%';
            }
        };

        const onMouseUp = () => {
            splitter.classList.remove('active');
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        };

        splitter.addEventListener('mousedown', onMouseDown);
    },

    initVertical: function (splitterId, leftPanelId, rightPanelId, initialLeftWidth) {
        const splitter = document.getElementById(splitterId);
        const leftPanel = document.getElementById(leftPanelId);
        const rightPanel = document.getElementById(rightPanelId);
        if (!splitter || !leftPanel || !rightPanel) return;

        let startX, startLeftWidth;

        if (initialLeftWidth) {
            leftPanel.style.width = initialLeftWidth + 'px';
        }

        const onMouseDown = (e) => {
            e.preventDefault();
            startX = e.clientX;
            startLeftWidth = leftPanel.getBoundingClientRect().width;
            splitter.classList.add('active');
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        };

        const onMouseMove = (e) => {
            const delta = e.clientX - startX;
            const newWidth = startLeftWidth + delta;
            if (newWidth > 150 && newWidth < window.innerWidth - 200) {
                leftPanel.style.width = newWidth + 'px';
            }
        };

        const onMouseUp = () => {
            splitter.classList.remove('active');
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        };

        splitter.addEventListener('mousedown', onMouseDown);
    }
};
