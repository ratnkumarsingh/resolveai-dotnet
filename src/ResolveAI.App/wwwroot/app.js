// Browser actions the C# components ask for.
window.resolveai = {
  scrollToId(id) {
    document.getElementById(id)?.scrollIntoView({ behavior: "smooth", block: "center" });
  },
  copy(text) {
    return navigator.clipboard.writeText(text);
  },
};
