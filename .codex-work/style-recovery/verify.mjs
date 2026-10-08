import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile } from "@oai/artifact-tool";

const root = path.resolve("../..");
const previewDir = path.resolve("previews");
await fs.mkdir(previewDir, { recursive: true });

const files = [
  ["cable-template", path.join(root, "Resources", "Templates", "cable_list_template.xlsx"), ["template"]],
  ["device-template", path.join(root, "Resources", "Templates", "subsystem_list_template.xlsx"), ["template"]],
  ["cable-output", path.join(root, "Test1", "cable", "cable_list.xlsx"), ["1111111", "ddddddddd"]],
  ["device-output", path.join(root, "Test1", "subsystem", "subsystem_list.xlsx"), ["dev_list"]],
];

for (const [label, filePath, expectedSheets] of files) {
  const workbook = await SpreadsheetFile.importXlsx(await fs.readFile(filePath));
  const sheetInfo = await workbook.inspect({ kind: "sheet", include: "id,name", maxChars: 4000 });
  const sheetText = sheetInfo.ndjson ?? String(sheetInfo);
  for (const expected of expectedSheets) {
    if (!sheetText.includes(expected)) throw new Error(`${label}: missing sheet ${expected}`);
    const formulaInfo = await workbook.inspect({
      kind: "formula",
      sheetId: expected,
      range: "A1:AZ300",
      maxChars: 4000,
      options: { maxResults: 200 },
    });
    const formulaText = formulaInfo.ndjson ?? String(formulaInfo);
    if (/#[A-Z]+[!?]?\b/.test(formulaText)) throw new Error(`${label}/${expected}: formula error detected`);

    const preview = await workbook.render({ sheetName: expected, autoCrop: "all", scale: 0.75, format: "png" });
    await fs.writeFile(path.join(previewDir, `${label}-${expected}.png`), new Uint8Array(await preview.arrayBuffer()));
  }

  console.log(`${label}: ${expectedSheets.join(", ")}; formula errors: 0`);
}
