// The language switch remembers the reader's choice; the English page reads it before rendering (see its <head>).
// auxclick covers a middle click, which opens the page in a new tab.
document.querySelectorAll("a[data-lang]").forEach(function (link) {
  function remember() {
    try { localStorage.setItem("appshelf-lang", link.dataset.lang); } catch (e) { /* storage blocked: nothing to remember */ }
  }
  link.addEventListener("click", remember);
  link.addEventListener("auxclick", remember);
});

// The promo loads nothing until it is on screen, then plays muted on its own, unless the reader prefers less motion
// or saves data; its controls are always there. Once the reader pauses it, it stays paused.
var promo = document.querySelector(".video video");
var lessMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
var saveData = navigator.connection && navigator.connection.saveData;
if (promo && !saveData && "IntersectionObserver" in window) {
  var pausing = false, readerPaused = false, visible = false;
  promo.addEventListener("pause", function () { if (!pausing) readerPaused = true; pausing = false; });
  promo.addEventListener("play", function () { readerPaused = false; });
  var pause = function () { if (!promo.paused) { pausing = true; promo.pause(); } };
  var update = function () {
    if (visible && !readerPaused && !lessMotion.matches) promo.play().catch(function () { /* autoplay refused: the controls remain */ });
    else pause();
  };
  lessMotion.addEventListener("change", update);
  new IntersectionObserver(function (entries) {
    visible = entries[entries.length - 1].isIntersecting;
    update();
  }, { threshold: 0.25 }).observe(promo);
}
