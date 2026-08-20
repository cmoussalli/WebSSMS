// Browser side of backup download/upload.
//
// Both directions go over plain HTTP rather than the Blazor circuit. A .bak is
// routinely gigabytes; pushing that through SignalR would buffer the whole thing
// in browser memory and trip the hub message limit. An <a download> hands the job
// to the browser's own download manager, and XHR gives us real upload progress.
window.WebSSMS = window.WebSSMS || {};

window.WebSSMS.FileTransfer = {
    _uploads: new Map(),

    // Kick off a download. The URL carries a ticket, never a file path.
    download: function (url, fileName) {
        const link = document.createElement('a');
        link.href = url;
        if (fileName) link.download = fileName;
        link.style.display = 'none';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    },

    // What the user picked, so Blazor can mint a ticket for the right name/size
    // before a single byte moves.
    getSelectedFile: function (inputId) {
        const input = document.getElementById(inputId);
        if (!input || !input.files || input.files.length === 0) return null;

        const file = input.files[0];
        return { name: file.name, size: file.size };
    },

    clearInput: function (inputId) {
        const input = document.getElementById(inputId);
        if (input) input.value = '';
    },

    // Streams the selected file to the ticket URL, reporting progress back into
    // .NET as it goes. Resolves with the server's JSON response.
    upload: function (inputId, url, dotNetRef) {
        return new Promise(function (resolve, reject) {
            const input = document.getElementById(inputId);
            if (!input || !input.files || input.files.length === 0) {
                reject('No file selected.');
                return;
            }

            const file = input.files[0];
            const request = new XMLHttpRequest();
            window.WebSSMS.FileTransfer._uploads.set(inputId, request);

            request.open('POST', url, true);
            request.setRequestHeader('Content-Type', 'application/octet-stream');

            request.upload.onprogress = function (event) {
                if (!dotNetRef || !event.lengthComputable) return;
                const percent = Math.round((event.loaded / event.total) * 100);
                dotNetRef.invokeMethodAsync('OnUploadProgress', percent, event.loaded, event.total)
                    .catch(function () { /* circuit went away mid-upload */ });
            };

            request.onload = function () {
                window.WebSSMS.FileTransfer._uploads.delete(inputId);

                if (request.status >= 200 && request.status < 300) {
                    resolve(request.responseText);
                    return;
                }

                let message = 'Upload failed (HTTP ' + request.status + ').';
                try {
                    const body = JSON.parse(request.responseText);
                    if (body && body.error) message = body.error;
                } catch (e) { /* not JSON -- keep the generic message */ }
                reject(message);
            };

            request.onerror = function () {
                window.WebSSMS.FileTransfer._uploads.delete(inputId);
                reject('The connection dropped during the upload.');
            };

            request.onabort = function () {
                window.WebSSMS.FileTransfer._uploads.delete(inputId);
                reject('Upload cancelled.');
            };

            request.send(file);
        });
    },

    cancelUpload: function (inputId) {
        const request = this._uploads.get(inputId);
        if (request) request.abort();
    }
};
