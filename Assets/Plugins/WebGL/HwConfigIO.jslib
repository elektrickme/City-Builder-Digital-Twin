// Browser file download / upload bridge for the debug-menu config Export/Import (WebGL only).
mergeInto(LibraryManager.library, {

  // Trigger a download of `content` as a file named `filename`.
  HwDownloadTextFile: function (filenamePtr, contentPtr) {
    try {
      var filename = UTF8ToString(filenamePtr);
      var content = UTF8ToString(contentPtr);
      var blob = new Blob([content], { type: "application/json" });
      var url = URL.createObjectURL(blob);
      var a = document.createElement("a");
      a.href = url;
      a.download = filename || "config.json";
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    } catch (e) {
      console.error("[HwConfigIO] HwDownloadTextFile failed", e);
    }
  },

  // Open a file picker. The chosen file's text is stashed on window.__hwPendingConfig,
  // which C# drains via HwTakePendingUpload (polling). Cleared first so a stale value isn't read.
  HwOpenTextFilePicker: function (acceptPtr) {
    try {
      var accept = UTF8ToString(acceptPtr);
      window.__hwPendingConfig = null;
      var input = document.createElement("input");
      input.type = "file";
      if (accept) input.accept = accept;
      input.onchange = function (e) {
        var file = e.target.files && e.target.files[0];
        if (!file) return;
        var reader = new FileReader();
        reader.onload = function (ev) { window.__hwPendingConfig = ev.target.result; };
        reader.onerror = function () { console.error("[HwConfigIO] file read failed"); };
        reader.readAsText(file);
      };
      input.click();
    } catch (e) {
      console.error("[HwConfigIO] HwOpenTextFilePicker failed", e);
    }
  },

  // Returns the pending upload text (and clears it), or 0/null when nothing is ready yet.
  HwTakePendingUpload: function () {
    var s = window.__hwPendingConfig;
    if (s === null || s === undefined) return 0;
    window.__hwPendingConfig = null;
    var size = lengthBytesUTF8(s) + 1;
    var buffer = _malloc(size);
    stringToUTF8(s, buffer, size);
    return buffer;
  }
});
