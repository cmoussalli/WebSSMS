// Database Diagram Canvas JavaScript Interop
window.WebSSMS = window.WebSSMS || {};

window.WebSSMS.Diagram = {
    canvas: null,
    tables: {},
    relationships: [],
    dragging: null,
    panning: false,
    panStart: { x: 0, y: 0 },
    offset: { x: 0, y: 0 },

    init: function (canvasId, dotnetRef) {
        this.canvas = document.getElementById(canvasId);
        if (!this.canvas) return;

        this.dotnetRef = dotnetRef;
        this.tables = {};
        this.relationships = [];

        // Pan support
        this.canvas.addEventListener('mousedown', (e) => {
            if (e.target === this.canvas || e.target.tagName === 'svg') {
                this.panning = true;
                this.panStart = { x: e.clientX - this.offset.x, y: e.clientY - this.offset.y };
                this.canvas.classList.add('dragging');
            }
        });

        document.addEventListener('mousemove', (e) => {
            if (this.panning) {
                this.offset.x = e.clientX - this.panStart.x;
                this.offset.y = e.clientY - this.panStart.y;
                this._updateTransform();
            }
            if (this.dragging) {
                const table = this.tables[this.dragging.id];
                if (table) {
                    table.x = e.clientX - this.dragging.offsetX;
                    table.y = e.clientY - this.dragging.offsetY;
                    this._updateTablePosition(this.dragging.id);
                    this._updateRelationships();
                }
            }
        });

        document.addEventListener('mouseup', () => {
            this.panning = false;
            this.dragging = null;
            this.canvas.classList.remove('dragging');
        });
    },

    addTable: function (id, name, columns, x, y) {
        this.tables[id] = { id, name, columns, x, y };
        this._renderTable(id);
    },

    addRelationship: function (fromTableId, fromColumn, toTableId, toColumn) {
        this.relationships.push({ fromTableId, fromColumn, toTableId, toColumn });
        this._updateRelationships();
    },

    removeTable: function (id) {
        const el = document.getElementById('diagram-table-' + id);
        if (el) el.remove();
        delete this.tables[id];
        this.relationships = this.relationships.filter(
            r => r.fromTableId !== id && r.toTableId !== id
        );
        this._updateRelationships();
    },

    clear: function () {
        this.tables = {};
        this.relationships = [];
        if (this.canvas) {
            this.canvas.innerHTML = '<svg id="diagram-svg" style="position:absolute;inset:0;width:100%;height:100%;pointer-events:none;"><defs><marker id="arrowhead" markerWidth="10" markerHeight="7" refX="10" refY="3.5" orient="auto"><polygon points="0 0, 10 3.5, 0 7" fill="var(--text-secondary)" /></marker></defs></svg>';
        }
    },

    _renderTable: function (id) {
        const table = this.tables[id];
        if (!table || !this.canvas) return;

        const el = document.createElement('div');
        el.id = 'diagram-table-' + id;
        el.className = 'diagram-table';
        el.style.left = table.x + 'px';
        el.style.top = table.y + 'px';

        let html = `<div class="diagram-table-header">${table.name}</div>`;
        table.columns.forEach(col => {
            const pkClass = col.isPrimaryKey ? ' pk' : '';
            html += `<div class="diagram-table-column${pkClass}">
                <span class="diagram-table-column-name">${col.isPrimaryKey ? '🔑 ' : ''}${col.name}</span>
                <span class="diagram-table-column-type">${col.dataType}</span>
            </div>`;
        });
        el.innerHTML = html;

        // Drag support
        el.addEventListener('mousedown', (e) => {
            if (e.target.closest('.diagram-table-header')) {
                e.stopPropagation();
                const rect = el.getBoundingClientRect();
                this.dragging = {
                    id,
                    offsetX: e.clientX - rect.left,
                    offsetY: e.clientY - rect.top
                };
            }
        });

        this.canvas.appendChild(el);
    },

    _updateTablePosition: function (id) {
        const table = this.tables[id];
        const el = document.getElementById('diagram-table-' + id);
        if (table && el) {
            el.style.left = table.x + 'px';
            el.style.top = table.y + 'px';
        }
    },

    _updateRelationships: function () {
        let svg = document.getElementById('diagram-svg');
        if (!svg) {
            svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            svg.id = 'diagram-svg';
            svg.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;pointer-events:none;';
            svg.innerHTML = '<defs><marker id="arrowhead" markerWidth="10" markerHeight="7" refX="10" refY="3.5" orient="auto"><polygon points="0 0, 10 3.5, 0 7" fill="var(--text-secondary)" /></marker></defs>';
            this.canvas.insertBefore(svg, this.canvas.firstChild);
        }

        // Remove existing lines
        svg.querySelectorAll('path.diagram-relationship').forEach(p => p.remove());

        this.relationships.forEach(rel => {
            const fromTable = this.tables[rel.fromTableId];
            const toTable = this.tables[rel.toTableId];
            if (!fromTable || !toTable) return;

            const fromEl = document.getElementById('diagram-table-' + rel.fromTableId);
            const toEl = document.getElementById('diagram-table-' + rel.toTableId);
            if (!fromEl || !toEl) return;

            const fromRect = fromEl.getBoundingClientRect();
            const toRect = toEl.getBoundingClientRect();
            const canvasRect = this.canvas.getBoundingClientRect();

            const x1 = fromRect.right - canvasRect.left;
            const y1 = fromRect.top + fromRect.height / 2 - canvasRect.top;
            const x2 = toRect.left - canvasRect.left;
            const y2 = toRect.top + toRect.height / 2 - canvasRect.top;

            const midX = (x1 + x2) / 2;
            const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
            path.setAttribute('class', 'diagram-relationship');
            path.setAttribute('d', `M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}`);
            svg.appendChild(path);
        });
    },

    _updateTransform: function () {
        // Could implement pan/zoom transform here
    }
};
