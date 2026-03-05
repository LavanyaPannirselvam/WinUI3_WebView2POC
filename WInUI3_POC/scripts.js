window.sharedBuffer = null;
window.saveStartTime = null;

// Segment metadata received from C# when the shared buffer is posted.
// segmentOffset: byte offset into the full 5 MB buffer where this window's 1 MB slice begins.
// segmentSize:   usable bytes in this window's slice (1 MB minus the header C# reserved).
window.segmentMeta = null;  // { windowId, segmentIndex, segmentOffset, segmentSize }

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
            let segmentOffset = 0;
            let segmentSize = 0;
            const textEncoder = new TextEncoder();

            self.onmessage = (e) => {
                const msg = e.data;

                if (msg.type === "init") {
                    sharedBuffer = msg.sharedBuffer;
                    segmentOffset = msg.segmentOffset;
                    segmentSize   = msg.segmentSize;
                    view = new Uint8Array(sharedBuffer);
                    console.log("[Worker] init: segmentOffset=" + segmentOffset + ", segmentSize=" + segmentSize);
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

                    if (bytes.length > segmentSize) {
                        self.postMessage({
                            type: "error",
                            message: \`Content exceeds segment. Required=\${bytes.length}, Capacity=\${segmentSize}\`
                        });
                        return;
                    }

                    // Write into this window's slice only (starting at segmentOffset)
                    view.set(bytes, segmentOffset);
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
                    bytes: msg.byteLength
                }));
            }
        };

        return saveWorker;
    }

    function initWorkerWithSharedBuffer() {
        const worker = ensureSaveWorker();

        if (!window.sharedBuffer || bufferTransferredToWorker) return;
        if (!window.segmentMeta) {
            console.error("initWorkerWithSharedBuffer: segmentMeta not set");
            return;
        }

        workerInited = false;

        // Transfer the full SharedArrayBuffer to the worker together with this
        // window's slice coordinates so the worker writes to the right region.
        worker.postMessage({
            type:          "init",
            sharedBuffer:  window.sharedBuffer,
            segmentOffset: window.segmentMeta.segmentOffset,
            segmentSize:   window.segmentMeta.segmentSize
        }, [window.sharedBuffer]);

        bufferTransferredToWorker = true;
        window.sharedBuffer = null;
    }

    window.chrome?.webview?.addEventListener?.('sharedbufferreceived', (e) => {
        if (e.additionalData) {
            const meta = e.additionalData;

            if (meta.type === "init") {
                window.sharedBuffer = e.getBuffer();
                window.segmentMeta  = meta;   // save { windowId, segmentIndex, segmentOffset, segmentSize }
                bufferTransferredToWorker = false;
                console.log(
                    "Shared buffer received: total byteLength=" + window.sharedBuffer.byteLength +
                    ", windowId="      + meta.windowId +
                    ", segmentIndex="  + meta.segmentIndex +
                    ", segmentOffset=" + meta.segmentOffset +
                    ", segmentSize="   + meta.segmentSize
                );
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