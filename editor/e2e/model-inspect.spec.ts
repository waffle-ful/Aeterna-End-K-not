import { test } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";

/**
 * モデル検分ツール。ゲームを起動したまま、__crewRun のシーンを別カメラ (game camera の clone) で
 * 覗いて接写を撮る。ゲームループの render を止めないので、撮影 → 即 toDataURL で 1 フレーム掠め取る。
 *
 * 注意: これは回帰ゲートではない (ROADMAP: 描画回帰は PIXI extract ハッシュが正典)。
 * 目的は「モデルの出来を人間が見る」ためのスクショ生成。
 */

// outputDir (.artifacts) は Playwright が毎回クリーンするので、撮影先は別ディレクトリにする。
const OUT = "e2e/shots";

interface Shot {
    name: string;
    data: string;
}
interface Result {
    shots: Shot[];
    measure: unknown;
}

test("crew / 敵 / ロボのモデル接写を撮る", async ({ page }) => {
    await page.goto("/?crdebug");
    await page.locator("#start-minigame").click();
    await page.locator("#minigame-overlay canvas").waitFor({ timeout: 30_000 });
    await page.waitForTimeout(3500); // 群衆が育つのを待つ

    const res = await page.evaluate(async (): Promise<Result | { err: string }> => {
        const g = (globalThis as Record<string, unknown>).__crewRun as Record<string, any> | undefined;
        if (!g) return { err: "__crewRun なし" };

        const renderer = g.renderer as { render: (s: unknown, c: unknown) => void; domElement: HTMLCanvasElement };
        const scene = g.scene;

        // ゲームループを止める。以降シーンを自由に触れる (この spec はテスト終了で破棄されるので復元不要)。
        if (typeof g.raf === "number") cancelAnimationFrame(g.raf);
        await new Promise((r) => setTimeout(r, 60));
        if (typeof g.raf === "number") cancelAnimationFrame(g.raf);
        const cam = g.camera.clone() as any;
        cam.fov = 28;
        cam.near = 0.05;

        const out: Shot[] = [];
        /** target を距離 dist・水平角 az(rad)・高さ h から見て 1 枚撮る。 */
        const shoot = (name: string, tx: number, ty: number, tz: number, dist: number, az: number, h: number): void => {
            cam.position.set(tx + Math.sin(az) * dist, ty + h, tz + Math.cos(az) * dist);
            cam.lookAt(tx, ty, tz);
            cam.updateProjectionMatrix();
            cam.updateMatrixWorld(true);
            renderer.render(scene, cam);
            out.push({ name, data: renderer.domElement.toDataURL("image/png") });
        };

        // --- 1) 味方クルー (群衆の先頭個体) ---
        // Member は隊列相対 (rx/ry/rz)。ワールド = (centroidX + rx, ry, CROWD_Z + rz)、CROWD_Z = 0。
        const members = g.members as { rx: number; ry: number; rz: number }[] | undefined;
        const m0 = members && members.length ? members[0] : { rx: 0, ry: 0, rz: 0 };
        const cx = (g.centroidX ?? 0) + m0.rx, cz = 0 + m0.rz;

        // まず群衆ぜんたい (シルエットの密度感) — 武器あり / 素体のみ の2枚で比較
        shoot("crowd-wide-armed", g.centroidX ?? 0, 0.9, cz, 8.0, Math.PI, 2.4);
        for (const k of ["weaponMesh", "armorMesh", "partyCrewMesh"]) {
            const mm = g[k] as { visible: boolean } | null;
            if (mm) mm.visible = false;
        }
        shoot("crowd-wide-naked", g.centroidX ?? 0, 0.9, cz, 8.0, Math.PI, 2.4);

        // --- 1体だけ残して接写 (ループ停止済みなので状態は保つ) ---
        const crowd = g.crowdMesh as { setVisibilityAt: (i: number, v: boolean) => void };
        const n = members?.length ?? 0;
        for (let i = 1; i < n; i++) crowd.setVisibilityAt(i, false);
        // 素体だけ見たいので重ね掛けメッシュ (武器/装甲/パーティゴア) を消す
        for (const k of ["weaponMesh", "armorMesh", "partyCrewMesh", "robotMesh"]) {
            const m = g[k] as { visible: boolean } | null;
            if (m) m.visible = false;
        }
        const solo = (name: string, az: number, h = 0.25, dist = 4.0) => {
            shoot(name, cx, 0.85, cz, dist, az, h);
        };
        solo("crew-front", Math.PI);
        solo("crew-side", Math.PI / 2);
        solo("crew-back", 0);
        solo("crew-3q", Math.PI * 0.78);
        solo("crew-hi", Math.PI * 0.8, 2.0, 4.4);
        solo("crew-head", Math.PI, 0.9, 1.9); // 顔まわり寄り

        // --- 標準アーマー (既定で常時装備) を1体分だけ戻して素体との位置合わせを見る ---
        for (const k of ["armorMesh", "weaponMesh"]) {
            const m = g[k] as { visible: boolean; count: number } | null;
            if (m) { m.visible = true; m.count = 1; }
        }
        shoot("armored-front", cx, 0.85, cz, 4.0, 0, 0.25);
        shoot("armored-side", cx, 0.85, cz, 4.0, Math.PI / 2, 0.25);
        shoot("armored-3q", cx, 0.85, cz, 4.0, Math.PI * 0.22, 0.5);

        // --- 2) 敵まわり: entities から代表を1体ずつ ---
        // King boss: its 16 guards now reuse the ally body in red. Normally it only appears late,
        // so build one directly instead of playing to it.
        try {
            const king = (g as { buildKingBoss: () => { obj: any } }).buildKingBoss();
            king.obj.position.set(0, 0, 14);
            scene.add(king.obj);
            shoot("boss-king", 0, 2.2, 14, 15.0, Math.PI, 3.0);
            shoot("boss-king-guards", 0, 1.0, 14, 9.0, Math.PI, 1.2);
            scene.remove(king.obj);
        } catch (e) {
            console.log("king boss shot failed:", String(e));
        }
        // Turret (archer): spawn one directly and take a close-up.
        try {
            (g as { spawnArcher: (b?: boolean, s?: boolean) => void }).spawnArcher(false, false);
            const ar = ((g.entities as any[]) ?? []).filter((e) => String(e.type) === "archer").pop();
            if (ar?.obj?.position) {
                shoot("archer", ar.obj.position.x, ar.obj.position.y + 0.6, ar.obj.position.z, 3.2, Math.PI, 0.7);
            }
        } catch (e) {
            console.log("archer shot failed:", String(e));
        }

        const ents = (g.entities as any[]) ?? [];
        const seen = new Set<string>();
        for (const e of ents) {
            const kind = String(e.kind ?? e.type ?? "?");
            if (seen.has(kind) || seen.size >= 6) continue;
            const obj = e.obj ?? e.mesh ?? e.group ?? e.root;
            if (!obj || !obj.position) continue;
            seen.add(kind);
            shoot(`enemy-${kind}`, obj.position.x, obj.position.y + 0.6, obj.position.z, 4.0, Math.PI, 0.8);
        }

        // --- 計測: 素体の実バウンド vs ボーン world y vs 装甲プレートの想定位置 ---
        const geo = g.crewBodyGeo as any;
        geo.computeBoundingBox();
        const bb = geo.boundingBox;
        const bones = (g.crewBones as any[]) ?? [];
        const boneY: Record<string, number> = {};
        for (const b of bones) {
            b.updateMatrixWorld(true);
            boneY[b.name] = Number(b.getWorldPosition(b.position.clone()).y.toFixed(4));
        }
        const measure = {
            bodyBBox: { minY: +bb.min.y.toFixed(4), maxY: +bb.max.y.toFixed(4), minX: +bb.min.x.toFixed(4), maxX: +bb.max.x.toFixed(4), minZ: +bb.min.z.toFixed(4), maxZ: +bb.max.z.toFixed(4) },
            vertexCount: geo.getAttribute("position").count,
            boneWorldY: boneY,
        };
        return { shots: out, measure };
    });

    if (!("shots" in res)) {
        console.log("失敗:", JSON.stringify(res));
        return;
    }
    const shots = res.shots;
    console.log("=== 計測 ===\n" + JSON.stringify(res.measure, null, 2));
    mkdirSync(OUT, { recursive: true });
    for (const s of shots) {
        writeFileSync(`${OUT}/${s.name}.png`, Buffer.from(s.data.split(",")[1], "base64"));
    }
    console.log(`撮影 ${shots.length} 枚: ${shots.map((s) => s.name).join(", ")}`);
});
