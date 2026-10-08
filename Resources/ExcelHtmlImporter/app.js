(async function () {
  "use strict";

  const status = document.getElementById("status");
  const tabs = document.getElementById("tabs");
  const preview = document.getElementById("preview");
  const confirm = document.getElementById("confirm");
  let workbook = null;

  function childrenByName(parent, name) {
    return Array.from(parent.getElementsByTagName("*")).filter(node => node.localName === name);
  }

  function parseXml(text) {
    const documentValue = new DOMParser().parseFromString(text, "application/xml");
    if (documentValue.getElementsByTagName("parsererror").length)
      throw new Error("Excel 내부 XML을 해석하지 못했습니다.");
    return documentValue;
  }

  function resolveZipPath(base, target) {
    const parts = (base + "/" + target).split("/");
    const result = [];
    for (const part of parts) {
      if (!part || part === ".") continue;
      if (part === "..") result.pop(); else result.push(part);
    }
    return result.join("/");
  }

  function columnIndex(reference) {
    const letters = (reference.match(/^[A-Za-z]+/) || [""])[0].toUpperCase();
    let result = 0;
    for (const letter of letters) result = result * 26 + letter.charCodeAt(0) - 64;
    return Math.max(0, result - 1);
  }

  async function readSharedStrings(zip) {
    const entry = zip.file("xl/sharedStrings.xml");
    if (!entry) return [];
    const xml = parseXml(await entry.async("string"));
    return childrenByName(xml, "si").map(item =>
      childrenByName(item, "t").map(node => node.textContent || "").join(""));
  }

  function cellText(cell, sharedStrings) {
    const type = cell.getAttribute("t") || "";
    if (type === "inlineStr")
      return childrenByName(cell, "t").map(node => node.textContent || "").join("");
    const valueNode = childrenByName(cell, "v")[0];
    const value = valueNode ? valueNode.textContent || "" : "";
    if (type === "s") return sharedStrings[Number(value)] || "";
    if (type === "b") return value === "1" ? "TRUE" : "FALSE";
    return value;
  }

  async function parseWorkbook(arrayBuffer) {
    const zip = await JSZip.loadAsync(arrayBuffer);
    const workbookEntry = zip.file("xl/workbook.xml");
    const relationshipsEntry = zip.file("xl/_rels/workbook.xml.rels");
    if (!workbookEntry || !relationshipsEntry)
      throw new Error("올바른 XLSX 통합 문서가 아닙니다.");

    const workbookXml = parseXml(await workbookEntry.async("string"));
    const relationshipsXml = parseXml(await relationshipsEntry.async("string"));
    const relationshipMap = new Map(childrenByName(relationshipsXml, "Relationship").map(item =>
      [item.getAttribute("Id"), item.getAttribute("Target")]));
    const sharedStrings = await readSharedStrings(zip);
    const sheets = [];
    let totalCells = 0;

    for (const sheet of childrenByName(workbookXml, "sheet")) {
      const relationshipId = sheet.getAttribute("r:id") ||
        sheet.getAttributeNS("http://schemas.openxmlformats.org/officeDocument/2006/relationships", "id");
      const target = relationshipMap.get(relationshipId);
      if (!target) continue;
      const path = resolveZipPath("xl", target);
      const entry = zip.file(path);
      if (!entry) continue;
      const sheetXml = parseXml(await entry.async("string"));
      const sparseRows = [];
      let maxColumn = -1;
      let maxRow = -1;
      for (const rowNode of childrenByName(sheetXml, "row")) {
        const declaredRow = Number(rowNode.getAttribute("r") || "0") - 1;
        const rowIndex = declaredRow >= 0 ? declaredRow : sparseRows.length;
        if (!Number.isInteger(rowIndex) || rowIndex >= 100000)
          throw new Error("100,000행 이하의 Excel 파일만 가져올 수 있습니다.");
        const values = sparseRows[rowIndex] || [];
        for (const cell of childrenByName(rowNode, "c")) {
          const col = columnIndex(cell.getAttribute("r") || "A1");
          if (!Number.isInteger(col) || col >= 16384)
            throw new Error("Excel 열 범위가 너무 큽니다.");
          values[col] = cellText(cell, sharedStrings);
          maxColumn = Math.max(maxColumn, col);
        }
        sparseRows[rowIndex] = values;
        maxRow = Math.max(maxRow, rowIndex);
      }
      const rows = [];
      totalCells += (maxRow + 1) * (maxColumn + 1);
      if (totalCells > 2000000) throw new Error("Excel 데이터가 2,000,000셀 한도를 넘습니다.");
      for (let rowIndex = 0; rowIndex <= maxRow; rowIndex++) {
        const source = sparseRows[rowIndex] || [];
        const row = [];
        for (let col = 0; col <= maxColumn; col++) row.push(source[col] == null ? "" : String(source[col]));
        rows.push(row);
      }
      while (rows.length && rows[rows.length - 1].every(value => value === "")) rows.pop();
      sheets.push({ name: sheet.getAttribute("name") || "Sheet", rows: rows });
    }
    return { sheets: sheets };
  }

  function columnName(index) {
    let value = index + 1;
    let result = "";
    while (value > 0) {
      value--;
      result = String.fromCharCode(65 + value % 26) + result;
      value = Math.floor(value / 26);
    }
    return result;
  }

  function showSheet(index) {
    const sheet = workbook.sheets[index];
    Array.from(tabs.children).forEach((button, buttonIndex) =>
      button.classList.toggle("active", buttonIndex === index));
    preview.replaceChildren();
    const visibleRows = sheet.rows.slice(0, 80);
    const maxColumns = Math.min(40, visibleRows.reduce((max, row) => Math.max(max, row.length), 0));
    const head = document.createElement("tr");
    const corner = document.createElement("th");
    corner.className = "row";
    head.appendChild(corner);
    for (let col = 0; col < maxColumns; col++) {
      const th = document.createElement("th");
      th.textContent = columnName(col);
      head.appendChild(th);
    }
    preview.appendChild(head);
    visibleRows.forEach((row, rowIndex) => {
      const tr = document.createElement("tr");
      const number = document.createElement("th");
      number.className = "row";
      number.textContent = String(rowIndex + 1);
      tr.appendChild(number);
      for (let col = 0; col < maxColumns; col++) {
        const td = document.createElement("td");
        td.textContent = row[col] || "";
        tr.appendChild(td);
      }
      preview.appendChild(tr);
    });
  }

  function buildTabs() {
    tabs.replaceChildren();
    workbook.sheets.forEach((sheet, index) => {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = sheet.name;
      button.addEventListener("click", () => showSheet(index));
      tabs.appendChild(button);
    });
    if (workbook.sheets.length) showSheet(0);
  }

  confirm.addEventListener("click", async () => {
    confirm.disabled = true;
    status.textContent = "프로그램으로 데이터를 전달하고 있습니다…";
    try {
      const response = await fetch("result", {
        method: "POST",
        headers: { "Content-Type": "application/json; charset=utf-8" },
        body: JSON.stringify(workbook)
      });
      if (!response.ok) throw new Error(await response.text());
      status.textContent = "전달이 완료되었습니다. 이 창을 닫아도 됩니다.";
    } catch (error) {
      status.textContent = "전달 실패: " + error.message;
      status.classList.add("error");
      confirm.disabled = false;
    }
  });

  try {
    const response = await fetch("workbook");
    if (!response.ok) throw new Error("프로그램에서 Excel 파일을 받지 못했습니다.");
    workbook = await parseWorkbook(await response.arrayBuffer());
    if (!workbook.sheets.length) throw new Error("읽을 수 있는 시트가 없습니다.");
    buildTabs();
    const fileName = new URLSearchParams(location.search).get("name") || "Excel 파일";
    status.textContent = `${fileName} · ${workbook.sheets.length}개 시트를 정상적으로 읽었습니다.`;
    confirm.disabled = false;
  } catch (error) {
    status.textContent = "Excel 해석 실패: " + error.message;
    status.classList.add("error");
  }
})();
