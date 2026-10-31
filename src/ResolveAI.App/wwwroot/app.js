// Browser actions the C# components ask for.
window.resolveai = {
  scrollToId(id, block = "center") {
    document.getElementById(id)?.scrollIntoView({ behavior: "smooth", block });
  },
  copy(text) {
    return navigator.clipboard.writeText(text);
  },
};
