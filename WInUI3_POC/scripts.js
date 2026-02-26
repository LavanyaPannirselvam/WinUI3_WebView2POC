// editorInterop.js

window.sharedBuffer = null;
window.saveStartTime = 0;
window.view = null;
window.lengthView = null;
window.textEncoder = null;
window.headerSize = 4;


// Listen for shared buffer initialization from C#
window.chrome.webview.addEventListener('sharedbufferreceived', e => {
    //window.chrome.webview.postMessage(JSON.stringify({
    //    type: "shared buffer received",
    //    text: "Received shared buffer from C#"
    //}));
    console.log("Received shared buffer from C#");
    console.log("Full event object:", e);
    console.log("additionalDataAsJson:", e.additionalData);
    console.log("buffer:", e.getBuffer());
    if (e.additionalData) {
        console.log("Received additional data with shared buffer");

        const meta = e.additionalData;
        console.log("Shared buffer metadata:", meta);
        if (meta.type === "init") {
            // Store the buffer reference for later use
            window.sharedBuffer = e.getBuffer();
            window.view = new Uint8Array(window.sharedBuffer);
            window.lengthView = new DataView(window.sharedBuffer);
            window.textEncoder = new TextEncoder();
            console.log("Shared buffer initialized with size", window.sharedBuffer.byteLength);
        }
    }
});

// Set up notifications for live typing
function setupEditorNotifications() {
    const editor = document.getElementById("editor");
    //if (editor) {
    //    editor.addEventListener("input", e => {
    //        window.chrome.webview.postMessage(JSON.stringify({
    //            type: "userInput",
    //            text: e.target.innerText
    //        }));
    //    });
    //}
}

// Write full content into the shared buffer and notify C#
function sendFullContent() {
    if (!window.sharedBuffer) {
        console.error("Shared buffer not initialized");
        return;
    }
    console.log("shared buffer available");
    console.log("sharedBuffer object:", window.sharedBuffer);
    console.log("sharedBuffer object size:", window.sharedBuffer.byteLength);
    const editor = document.getElementById("editor");
    if (!editor) {
        console.log("Editor not available");
        return;
    }
    const text = editor.innerText;
    const bytes = window.textEncoder.encode(text);

    const totalRequiredSize = window.headerSize + bytes.length;
 //   const bufferCapacity = window.sharedBuffer.byteLength;

 //   console.log("Buffer capacity:", bufferCapacity);
    console.log("Content byte size:", bytes.length);
    //if (bytes.length + headerSize > window.sharedBuffer.byteLength) {
    //    console.error("Content exceeds shared buffer size");
    //    return;
    //}

    // Write into the existing buffer
    
    window.lengthView.setUint32(0, bytes.length, true);
    window.view.set(bytes, window.headerSize);

    const endTime = performance.now();
    const duration = endTime - window.saveStartTime;
    console.log(`Full content sent to shared buffer in ${duration.toFixed(2)} ms`);

    //Notify C# that full content is ready
    window.chrome.webview.postMessage(JSON.stringify({ type: "fullContentReady", time: duration }));

   
}

// Placeholder function for periodic content retrieval
window.getContent = function() {
    console.log("getContent called");
    // Could return content here if needed
}

// Initialize when DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setupEditorNotifications);
} else {
    setupEditorNotifications();
}
function onSaveButtonClick() {
    console.log("Save button clicked");
    window.saveStartTime = performance.now();
}