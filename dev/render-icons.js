// Draws the page's PNG icons from the SVG files in Jellyfin.Plugin.Retimer/Web/icons/.
// Run it after changing one of those files: node dev/render-icons.js
// Needs Node and Playwright with its Chromium (npm install playwright, npx playwright install chromium).
'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const { chromium } = require('playwright');

const dir = path.join(__dirname, '..', 'Jellyfin.Plugin.Retimer', 'Web', 'icons');

// Each PNG the plugin serves, the SVG it is drawn from, and its size in pixels.
const outputs = [
    ['icon-192.png', 'icon.svg', 192],
    ['icon-512.png', 'icon.svg', 512],
    ['icon-maskable-192.png', 'icon-maskable.svg', 192],
    ['icon-maskable-512.png', 'icon-maskable.svg', 512],
    ['apple-touch-icon.png', 'icon-maskable.svg', 180],
    ['favicon-32.png', 'favicon.svg', 32],
];

(async () => {
    const browser = await chromium.launch();
    try {
        for (const [name, source, size] of outputs) {
            const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
            const svg = fs.readFileSync(path.join(dir, source));
            await page.setContent(
                '<!DOCTYPE html><html><body style="margin:0;background:transparent">'
                + `<img src="data:image/svg+xml;base64,${svg.toString('base64')}" width="${size}" height="${size}" style="display:block">`
                + '</body></html>');
            await page.locator('img').evaluate((img) => img.decode());
            const png = shrink(await page.screenshot({ omitBackground: true }));
            fs.writeFileSync(path.join(dir, name), png);
            await page.close();
            console.log(`${name}: ${size} px from ${source}, ${png.length} bytes`);
        }
    } finally {
        await browser.close();
    }
})();

// Chromium writes its PNGs for speed, not size. Packing the same pixels again at the strongest
// compression makes the icons several times smaller, without changing a pixel.
function shrink(png) {
    const kept = [];
    const data = [];
    for (let at = 8; at < png.length;) {
        const length = png.readUInt32BE(at);
        const type = png.toString('ascii', at + 4, at + 8);
        const body = png.subarray(at + 8, at + 8 + length);
        if (type === 'IDAT') {
            data.push(body);
        } else {
            kept.push([type, body]);
        }
        at += 12 + length;
    }

    const pixels = zlib.deflateSync(zlib.inflateSync(Buffer.concat(data)), { level: 9, memLevel: 9 });
    const parts = [png.subarray(0, 8)];
    for (const [type, body] of kept) {
        if (type === 'IEND') {
            parts.push(chunk('IDAT', pixels));
        }
        parts.push(chunk(type, body));
    }

    return Buffer.concat(parts);
}

function chunk(type, body) {
    const head = Buffer.alloc(8);
    head.writeUInt32BE(body.length, 0);
    head.write(type, 4, 'ascii');
    const check = Buffer.alloc(4);
    check.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), body])), 0);
    return Buffer.concat([head, body, check]);
}

function crc32(bytes) {
    let crc = 0xffffffff;
    for (const byte of bytes) {
        crc ^= byte;
        for (let bit = 0; bit < 8; bit++) {
            crc = crc & 1 ? 0xedb88320 ^ (crc >>> 1) : crc >>> 1;
        }
    }

    return (crc ^ 0xffffffff) >>> 0;
}
