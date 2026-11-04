(() => {
  const t0 = performance.now();
  const trace = (window.__trace = []);
  const at = () => (performance.now() - t0).toFixed(1);
  const conv = () => document.querySelector("#shell [data-conversation]");
  const first = conv();
  const sent = () => (globalThis.__benchScrollSent ? globalThis.__benchScrollSent() : -1);
  const stack = () => new Error().stack.split("\n").slice(2, 7).map(s => s.trim().replace(/https?:\/\/[^/]+\//, "")).join(" < ");
  const desc = Object.getOwnPropertyDescriptor(Element.prototype, "scrollTop");
  Object.defineProperty(Element.prototype, "scrollTop", {
    get() { return desc.get.call(this); },
    set(v) {
      if (this === conv() || this === first) trace.push(`${at()} WRITE scrollTop=${v} was=${desc.get.call(this)} same=${this === first} ${stack()}`);
      desc.set.call(this, v);
    },
    configurable: true,
  });
  for (const name of ["scrollTo", "scroll", "scrollBy", "scrollIntoView"]) {
    const orig = Element.prototype[name];
    Element.prototype[name] = function (...a) {
      trace.push(`${at()} ${name} ${this.tagName}${this === conv() ? "(conv)" : ""} ${JSON.stringify(a)} ${stack()}`);
      return orig.apply(this, a);
    };
  }
  document.addEventListener("scroll", e => {
    if (e.target === conv()) trace.push(`${at()} scroll-event top=${conv().scrollTop}`);
  }, true);
  document.addEventListener("scrollend", e => {
    if (e.target === conv()) trace.push(`${at()} scrollend top=${conv().scrollTop}`);
  }, true);
  new ResizeObserver(es => es.forEach(e => trace.push(`${at()} resize ${e.target.tagName} ${e.contentRect.height}`))).observe(first);
  new MutationObserver(ms => {
    for (const m of ms) for (const n of m.removedNodes) if (n.nodeType === 1 && (n === first || n.contains(first))) trace.push(`${at()} CONVERSATION REMOVED`);
  }).observe(document.body, { childList: true, subtree: true });
  let n = 0;
  const frame = () => {
    const c = conv();
    trace.push(`${at()} frame top=${c && c.scrollTop} h=${c && c.scrollHeight} ch=${c && c.clientHeight} sent=${sent()} same=${c === first}`);
    if (++n < 40) requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
})();
