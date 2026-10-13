// QA 截图强制主题（决策 446）：无头 Chrome 里 prefers-color-scheme 仿真不一定传到 Avalonia
// 的 PlatformSettings，单靠 emulateMediaFeatures 会出现「--theme dark 截出来却是浅色」。
// 这里按快捷键 Ctrl+Shift+S（跟随系统 → 浅色 → 深色）轮转，直到画面背景亮度与目标一致。
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function backgroundLuma(page, geo) {
    // 取聊天区右上一小块（背景，不含文字）求平均亮度。
    const clip = { x: geo.x + geo.w - 40, y: geo.y + 60, width: 16, height: 16 };
    const b64 = await page.screenshot({ clip, encoding: 'base64' });
    return await page.evaluate(async (data) => {
        const img = new Image();
        img.src = 'data:image/png;base64,' + data;
        await img.decode();
        const c = document.createElement('canvas');
        c.width = img.width; c.height = img.height;
        const ctx = c.getContext('2d');
        ctx.drawImage(img, 0, 0);
        const px = ctx.getImageData(0, 0, c.width, c.height).data;
        let sum = 0;
        for (let i = 0; i < px.length; i += 4) sum += 0.2126 * px[i] + 0.7152 * px[i + 1] + 0.0722 * px[i + 2];
        return sum / (px.length / 4);
    }, b64);
}

async function forceTheme(page, geo, theme) {
    const wantDark = theme === 'dark';
    for (let i = 0; i < 4; i++) {
        const luma = await backgroundLuma(page, geo);
        const isDark = luma < 110;
        if (isDark === wantDark) {
            console.log(`theme: ${theme} (luma ${luma.toFixed(0)})`);
            return true;
        }
        await page.keyboard.down('Control');
        await page.keyboard.down('Shift');
        await page.keyboard.press('KeyS');
        await page.keyboard.up('Shift');
        await page.keyboard.up('Control');
        await sleep(900);
    }
    throw new Error(`could not force ${theme} theme`);
}

module.exports = { forceTheme, backgroundLuma };
