(function () {
  "use strict";

  // ---------------------------------------------------------------- the scene, played once

  function playScene() {
    var scene = document.getElementById("scene");
    if (!scene || window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

    var codeEl = document.getElementById("code-text");
    var state = document.getElementById("hub-state");
    var count = document.getElementById("assist-count");
    var code = codeEl.textContent;

    scene.dataset.step = "0";
    codeEl.textContent = "";
    state.textContent = "off";
    count.textContent = "0 connected";

    setTimeout(function () {
      scene.dataset.step = "1";
      var i = 0;
      var typing = setInterval(function () {
        i++;
        codeEl.textContent = code.slice(0, i);
        if (i < code.length) return;
        clearInterval(typing);
        setTimeout(function () {
          scene.dataset.step = "2";
          state.textContent = "pairing";
          setTimeout(function () {
            scene.dataset.step = "3";
            state.textContent = "connected";
            count.textContent = "1 connected";
          }, 1600);
        }, 500);
      }, 55);
    }, 500);
  }

  // ---------------------------------------------------------------- downloads

  function size(bytes) {
    return Math.max(1, Math.round(bytes / 1048576)) + " MB";
  }

  var KINDS = {
    setup: { label: "Setup", what: "Installs for you, updates itself" },
    portable: { label: "Portable", what: "Zip, run from the folder" }
  };

  function renderApp(list, files) {
    list.textContent = "";
    ["setup", "portable"].forEach(function (kind, index) {
      var f = files[kind];
      if (!f) return;
      var li = document.createElement("li");
      var a = document.createElement("a");
      a.className = "file" + (index === 0 ? " primary" : "");
      a.href = "downloads/" + encodeURIComponent(f.file);
      a.setAttribute("download", "");
      a.title = f.file;

      var k = document.createElement("span");
      k.className = "kind";
      k.textContent = KINDS[kind].label;
      var what = document.createElement("span");
      what.className = "what";
      what.textContent = KINDS[kind].what;
      var sz = document.createElement("span");
      sz.className = "size";
      sz.textContent = size(f.size);

      a.appendChild(k); a.appendChild(sz); a.appendChild(what);
      li.appendChild(a);
      list.appendChild(li);
    });
  }

  function renderDownloads(d) {
    if (!d || !d.apps) return;
    document.querySelectorAll(".files[data-app]").forEach(function (list) {
      var files = d.apps[list.dataset.app];
      if (files) renderApp(list, files);
    });
    var built = new Date(d.built);
    document.getElementById("dl-version").textContent =
      "Version " + d.version + ", " + built.toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric" }) +
      ". Windows, 64-bit.";
  }

  fetch("downloads/downloads.json", { cache: "no-cache" })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(renderDownloads)
    .catch(function () { /* nothing published yet: the lists say so */ });

  // ---------------------------------------------------------------- share link

  var shareUrl = location.origin && location.protocol.indexOf("http") === 0
    ? location.origin + location.pathname.replace(/index\.html$/, "") + "#assist"
    : "https://greysource.eu/avh/#assist";
  var shareText = document.getElementById("share-url");
  var shareButton = document.getElementById("share-copy");
  shareText.textContent = shareUrl.replace(/^https?:\/\//, "");

  shareButton.addEventListener("click", function () {
    var done = function () {
      shareButton.textContent = "Copied";
      setTimeout(function () { shareButton.textContent = "Copy link"; }, 1800);
    };
    if (navigator.clipboard) navigator.clipboard.writeText(shareUrl).then(done, function () {});
  });

  playScene();
})();
