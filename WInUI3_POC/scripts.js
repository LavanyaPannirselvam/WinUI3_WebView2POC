window.sharedBuffer = null;
window.saveStartTime = null;

window.getContent = function () {
    console.log("getContent called");
};

function setupEditorNotifications() {
    // reserved
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setupEditorNotifications);
} else {
    setupEditorNotifications();
}

function onSaveButtonClick() {
    console.log("Save button clicked");
    window.saveStartTime = performance.now();
}

(function () {

    let saveWorker = null;
    let workerInited = false;
    let pendingWriteMsg = null;
    let bufferTransferredToWorker = false;

    function ensureSaveWorker() {
        if (saveWorker) return saveWorker;

        const workerSource = `
            let sharedBuffer = null;
            let view = null;
            const textEncoder = new TextEncoder();

            self.onmessage = (e) => {
                const msg = e.data;

                if (msg.type === "init") {
                    sharedBuffer = msg.sharedBuffer;
                    view = new Uint8Array(sharedBuffer);
                    self.postMessage({ type: "inited" });
                    return;
                }

                if (msg.type === "writeText") {
                    if (!sharedBuffer || !view) {
                        self.postMessage({ type: "error", message: "Worker not initialized" });
                        return;
                    }

                    const start = performance.now();
                    const bytes = textEncoder.encode(msg.text);

                    if (bytes.length > sharedBuffer.byteLength) {
                        self.postMessage({
                            type: "error",
                            message: \`Content exceeds buffer. Required=\${bytes.length}, Capacity=\${sharedBuffer.byteLength}\`
                        });
                        return;
                    }

                    // Write directly from offset 0 (NO HEADER)
                    view.set(bytes, 0);
                    console.log("First 10 bytes after write from worker thread:",Array.from(new Uint8Array(sharedBuffer, 0, 10)));
                    const end = performance.now();

                    self.postMessage({
                        type: "done",
                        byteLength: bytes.length,
                        durationMs: end - start
                    });
                }
            };
        `;

        const blob = new Blob([workerSource], { type: "text/javascript" });
        const workerUrl = URL.createObjectURL(blob);
        saveWorker = new Worker(workerUrl);

        saveWorker.onmessage = (e) => {
            const msg = e.data;

            if (msg.type === "inited") {
                workerInited = true;

                if (pendingWriteMsg) {
                    saveWorker.postMessage(pendingWriteMsg);
                    pendingWriteMsg = null;
                }
                return;
            }

            if (msg.type === "error") {
                console.error("Save worker error:", msg.message);
                window.chrome?.webview?.postMessage?.(JSON.stringify({
                    type: "fullContentError",
                    message: msg.message
                }));
                return;
            }

            if (msg.type === "done") {
                window.chrome.webview.postMessage(JSON.stringify({
                    type: "fullContentReady",
                    time: msg.durationMs,
                    bytes: msg.byteLength   // length passed via notification
                }));
                //window.chrome.webview.postSharedBuffer(window.sharedBuffer);
            }
        };

        return saveWorker;
    }

    function initWorkerWithSharedBuffer() {
        const worker = ensureSaveWorker();

        if (!window.sharedBuffer || bufferTransferredToWorker) return;

        workerInited = false;

        worker.postMessage({
            type: "init",
            sharedBuffer: window.sharedBuffer
        }, [window.sharedBuffer]);

        bufferTransferredToWorker = true;
        window.sharedBuffer = null;
    }

    window.chrome?.webview?.addEventListener?.('sharedbufferreceived', (e) => {
        if (e.additionalData) {
            const meta = e.additionalData;

            if (meta.type === "init") {
                window.sharedBuffer = e.getBuffer();
                bufferTransferredToWorker = false;
                console.log("Shared buffer received with byteLength:", window.sharedBuffer.byteLength);
                initWorkerWithSharedBuffer();
            }
        }
    });

    window.sendFullContent = function () {
        if (!window.sharedBuffer && !workerInited && !bufferTransferredToWorker) {
            console.error("Shared buffer not initialized");
            return;
        }

        const editor = document.getElementById("editor");
        if (!editor) {
            console.log("Editor not available");
            return;
        }

        const text = editor.innerText;

        const worker = ensureSaveWorker();

        if (!workerInited) {
            pendingWriteMsg = { type: "writeText", text };
            initWorkerWithSharedBuffer();
            return;
        }

        worker.postMessage({ type: "writeText", text });
    };


})();


const editor = document.querySelector('[contenteditable]');
editor?.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') {
        e.preventDefault();

        document.execCommand(
            'insertHTML',
            false,
            '<div><br></div>'
        );
    }
});

editor?.addEventListener('paste', function (e) {
    e.preventDefault();

    const text = (e.clipboardData || window.clipboardData).getData('text/plain');
    if (!text) return;

    const lines = text.split(/\r?\n/);
    const html = lines
        .map((line, i) => {
            if (i === 0) return line ? line : '<br>';
            return `<div>${line || '<br>'}</div>`;
        })
        .join('');

    document.execCommand('insertHTML', false, html);
});