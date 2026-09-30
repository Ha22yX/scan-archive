/* Small, local Markdown renderer. All content is constructed as text/DOM nodes:
   raw HTML is displayed literally; remote images and executable URL schemes are never loaded. */
(function (root) {
  'use strict';
  const node = (tag, text) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; return n; };
  function inline(parent, source, onCitation, depth = 0) {
    if (depth > 16) { parent.append(document.createTextNode(source)); return; }
    const tokens = /\\([\\`*_[\]{}()#+.!>|~-])|`([^`\n]+)`|\[\[([a-f0-9]{32}):(\d+)\]\]|!\[([^\]\n]*)\]\([^\n)]*\)|\[([^\]\n]+)\]\(([^\s)]+)(?:\s+"[^"]*")?\)|\*\*([\s\S]+?)\*\*|__([\s\S]+?)__|~~([^\n]+?)~~|\*([^*\n]+?)\*|_([^_\n]+?)_/gi;
    let at = 0, match;
    while ((match = tokens.exec(source))) {
      parent.append(document.createTextNode(source.slice(at, match.index)));
      let child;
      if (match[1]) child = document.createTextNode(match[1]);
      else if (match[2]) child = node('code', match[2]);
      else if (match[3]) {
        const id = match[3], page = Number(match[4]);
        if (Number.isSafeInteger(page) && page >= 1) {
          child = node('button', `原文 · 第 ${page} 页`); child.type = 'button'; child.className = 'citation';
          child.addEventListener('click', () => onCitation?.(id, page));
        } else child = document.createTextNode(match[0]);
      } else if (match[5] !== undefined) child = node('span', match[5] ? `[图片：${match[5]}]` : '[图片]');
      else if (match[6]) {
        try {
          const url = new URL(match[7], location.href);
          if (!['http:', 'https:', 'mailto:'].includes(url.protocol)) throw Error('Unsupported URL');
          child = node('a'); child.href = url.href; child.target = '_blank'; child.rel = 'noopener noreferrer'; inline(child, match[6], onCitation, depth + 1);
        } catch { child = document.createTextNode(match[6]); }
      } else {
        child = node(match[8] || match[9] ? 'strong' : match[10] ? 'del' : 'em');
        inline(child, match[8] || match[9] || match[10] || match[11] || match[12], onCitation, depth + 1);
      }
      parent.append(child); at = tokens.lastIndex;
    }
    parent.append(document.createTextNode(source.slice(at)));
  }
  const listMatch = line => /^(\s*)([-+*]|\d+[.)])\s+(.*)$/.exec(line);
  const cells = line => line.trim().replace(/^\|/, '').replace(/\|$/, '').split(/(?<!\\)\|/).map(x => x.trim().replace(/\\\|/g, '|'));
  const tableRule = line => /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$/.test(line || '');
  const beginsBlock = (lines, i) => /^(\s*$| {0,3}#{1,6}\s| {0,3}>| {0,3}```| {0,3}~~~| {0,3}(?:-{3,}|\*{3,}|_{3,})\s*$)/.test(lines[i]) || listMatch(lines[i]) || tableRule(lines[i + 1]);
  function blocks(parent, lines, onCitation, depth = 0) {
    if (depth > 12) { parent.append(node('p', lines.join('\n'))); return; }
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (!line.trim()) { i++; continue; }
      const fence = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(line);
      if (fence) {
        const content = []; i++;
        while (i < lines.length && !lines[i].trim().startsWith(fence[1])) content.push(lines[i++]);
        if (i < lines.length) i++;
        const pre = node('pre'); pre.append(node('code', content.join('\n'))); parent.append(pre); continue;
      }
      const heading = /^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$/.exec(line);
      if (heading) { const h = node('h' + Math.min(heading[1].length + 1, 6)); inline(h, heading[2], onCitation); parent.append(h); i++; continue; }
      if (/^ {0,3}(?:-{3,}|\*{3,}|_{3,})\s*$/.test(line)) { parent.append(node('hr')); i++; continue; }
      if (/^ {0,3}>/.test(line)) {
        const quote = [], block = node('blockquote');
        while (i < lines.length && /^ {0,3}>/.test(lines[i])) quote.push(lines[i++].replace(/^ {0,3}> ?/, ''));
        blocks(block, quote, onCitation, depth + 1); parent.append(block); continue;
      }
      if (line.includes('|') && tableRule(lines[i + 1])) {
        const wrap = node('div'), table = node('table'), head = node('thead'), hr = node('tr'), body = node('tbody'); wrap.className = 'table-scroll';
        cells(line).forEach(value => { const th = node('th'); inline(th, value, onCitation); hr.append(th); }); head.append(hr); i += 2;
        while (i < lines.length && lines[i].includes('|') && lines[i].trim()) { const tr = node('tr'); cells(lines[i++]).forEach(value => { const td = node('td'); inline(td, value, onCitation); tr.append(td); }); body.append(tr); }
        table.append(head, body); wrap.append(table); parent.append(wrap); continue;
      }
      const first = listMatch(line);
      if (first) {
        const indent = first[1].length, ordered = /^\d/.test(first[2]), list = node(ordered ? 'ol' : 'ul');
        if (ordered) list.start = parseInt(first[2], 10);
        while (i < lines.length) {
          const item = listMatch(lines[i]);
          if (!item || item[1].length !== indent || /^\d/.test(item[2]) !== ordered) break;
          const li = node('li'), content = [item[3]]; i++;
          while (i < lines.length && lines[i].trim() && /^\s/.test(lines[i]) && (listMatch(lines[i])?.[1].length ?? Infinity) > indent) {
            const continuation = lines[i++]; content.push(continuation.slice(Math.min(indent + 2, continuation.search(/\S/))));
          }
          if (content.length === 1) inline(li, content[0], onCitation); else blocks(li, content, onCitation, depth + 1);
          list.append(li);
        }
        parent.append(list); continue;
      }
      const paragraph = [line]; i++;
      while (i < lines.length && !beginsBlock(lines, i)) paragraph.push(lines[i++]);
      const p = node('p'); inline(p, paragraph.join('\n'), onCitation); parent.append(p);
    }
  }
  root.renderMarkdown = function (target, source, onCitation) {
    target.replaceChildren(); target.classList.add('markdown');
    blocks(target, String(source || '').replace(/\r\n?/g, '\n').split('\n'), onCitation);
  };
  root.plainMarkdown = source => String(source || '').replace(/\[\[([a-f0-9]{32}):(\d+)\]\]/gi, '第 $2 页').replace(/^\s{0,3}#{1,6}\s+/gm, '').replace(/\*\*|__|~~|`/g, '').replace(/\[([^\]]+)\]\([^)]*\)/g, '$1').replace(/\s+/g, ' ').trim();
})(globalThis);
