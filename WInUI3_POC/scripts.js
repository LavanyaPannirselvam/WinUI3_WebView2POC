// editorInterop.js

let sharedBuffer = null;

// Listen for shared buffer initialization from C#
window.chrome.webview.addEventListener('sharedbufferreceived', e => {
    if (e.additionalDataAsJson) {
        const meta = JSON.parse(e.additionalDataAsJson);
        if (meta.type === "init") {
            // Store the buffer reference for later use
            sharedBuffer = e.buffer;
            console.log("Shared buffer initialized");
        }
    }
});

// Set up notifications for live typing
function setupEditorNotifications() {
    const editor = document.getElementById("editor");
    if (editor) {
        editor.addEventListener("input", e => {
            window.chrome.webview.postMessage({
                type: "userInput",
                text: e.target.innerText
            });
        });
    }
}

// Write full content into the shared buffer and notify C#
function sendFullContent() {
    if (!sharedBuffer) {
        console.error("Shared buffer not initialized");
        return;
    }

    const editor = document.getElementById("editor");
    const text = editor ? editor.innerText : "";
    const encoder = new TextEncoder();
    const bytes = encoder.encode(text);

    // Write into the existing buffer
    const view = new Uint8Array(sharedBuffer);
    view.set(bytes);

    // Notify C# that full content is ready
    window.chrome.webview.postMessage({ type: "fullContentReady" });
}

// Placeholder function for periodic content retrieval
function getContent() {
    console.log("getContent called");
    // Could return content here if needed
}

// Initialize when DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setupEditorNotifications);
} else {
    setupEditorNotifications();
}