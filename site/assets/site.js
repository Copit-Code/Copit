// Mobile nav, scroll reveal, latest release version. No dependencies.
(function () {
  document.documentElement.classList.remove("no-js");

  var toggle = document.querySelector(".nav-toggle");
  var nav = document.querySelector(".nav");
  if (toggle && nav) {
    toggle.addEventListener("click", function () {
      var open = nav.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
      toggle.textContent = open ? "Close" : "Menu";
    });
  }

  var items = document.querySelectorAll(".reveal");
  if ("IntersectionObserver" in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); }
      });
    }, { rootMargin: "0px 0px -8% 0px" });
    items.forEach(function (el) { io.observe(el); });
  } else {
    items.forEach(function (el) { el.classList.add("in"); });
  }

  // Show the latest release tag wherever [data-version] appears. Falls back silently.
  var slots = document.querySelectorAll("[data-version]");
  if (slots.length && window.fetch) {
    fetch("https://api.github.com/repos/Copit-Code/Copit/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (!j || !j.tag_name) return;
        slots.forEach(function (s) { s.textContent = j.tag_name; });
      })
      .catch(function () {});
  }

  var y = document.querySelector("[data-year]");
  if (y) y.textContent = new Date().getFullYear();
})();
