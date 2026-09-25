package dev.allofus.fusioncore.ui;

import android.graphics.Bitmap;
import android.graphics.BitmapShader;
import android.graphics.Matrix;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Shader;

import java.util.Random;

/**
 * Crayon-on-paper strokes: a paint whose ink skips where the paper grain is high, and
 * wobbly shapes that are redrawn a little differently on every frame (the "boil" of
 * hand-drawn animation).
 */
final class Crayon {
    /** Hand-drawn animation runs on twos-ish: 8 drawings per second. */
    static final long FRAME_MS = 125;

    private static final int GRAIN_SIZE = 64;

    private Crayon() {
    }

    static long frameNow() {
        return android.os.SystemClock.uptimeMillis() / FRAME_MS;
    }

    /** A round-capped stroke in {@code color} with paper grain showing through. */
    static Paint stroke(int color, float width) {
        Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeJoin(Paint.Join.ROUND);
        paint.setStrokeWidth(width);
        paint.setShader(grain(color));
        return paint;
    }

    /** Slides the paper under the stroke so each frame catches a different grain. */
    static void shiftGrain(Paint paint, long frame) {
        Shader shader = paint.getShader();
        if (shader == null) {
            return;
        }
        Matrix m = new Matrix();
        m.setTranslate((frame * 23) % GRAIN_SIZE, (frame * 37) % GRAIN_SIZE);
        shader.setLocalMatrix(m);
    }

    private static Shader grain(int color) {
        Random random = new Random(0x6B6E6F74L);
        float[] noise = new float[GRAIN_SIZE * GRAIN_SIZE];
        for (int i = 0; i < noise.length; i++) {
            noise[i] = random.nextFloat();
        }
        int[] pixels = new int[noise.length];
        int baseAlpha = color >>> 24;
        int rgb = color & 0x00FFFFFF;
        for (int y = 0; y < GRAIN_SIZE; y++) {
            for (int x = 0; x < GRAIN_SIZE; x++) {
                // Blur each pixel with its right neighbour and the row above, so the gaps
                // come out as short streaks rather than single-pixel salt.
                float n = noise[y * GRAIN_SIZE + x] * 0.5f
                        + noise[y * GRAIN_SIZE + (x + 1) % GRAIN_SIZE] * 0.3f
                        + noise[((y + GRAIN_SIZE - 1) % GRAIN_SIZE) * GRAIN_SIZE + x] * 0.2f;
                float ink = n < 0.34f ? 0.08f : n < 0.45f ? 0.55f : 0.8f + 0.2f * n;
                pixels[y * GRAIN_SIZE + x] = rgb | (Math.round(baseAlpha * ink) << 24);
            }
        }
        Bitmap bitmap = Bitmap.createBitmap(pixels, GRAIN_SIZE, GRAIN_SIZE, Bitmap.Config.ARGB_8888);
        return new BitmapShader(bitmap, Shader.TileMode.REPEAT, Shader.TileMode.REPEAT);
    }

    /** A horizontal line from x0 to x1 that wanders by up to {@code amp} as if drawn freehand. */
    static Path wobblyLine(float x0, float x1, float y, float amp, float step, long seed) {
        Random random = new Random(seed);
        Path path = new Path();
        float px = x0;
        float py = y + (random.nextFloat() - 0.5f) * amp;
        path.moveTo(px, py);
        float drift = 0f;
        for (float x = x0 + step; x < x1 + step; x += step) {
            float nx = Math.min(x, x1);
            drift = drift * 0.6f + (random.nextFloat() - 0.5f) * amp;
            float ny = y + drift;
            path.quadTo(px, py, (px + nx) / 2f, (py + ny) / 2f);
            px = nx;
            py = ny;
        }
        path.lineTo(px, py);
        return path;
    }

    /** A loose, not-quite-closed circle, like one drawn in a single hurried stroke. */
    static Path wobblyCircle(float cx, float cy, float r, float amp, long seed) {
        Random random = new Random(seed);
        Path path = new Path();
        int steps = 18;
        double start = random.nextFloat() * Math.PI * 2;
        double sweep = Math.PI * 2 * (1.04 + random.nextFloat() * 0.08);
        for (int i = 0; i <= steps; i++) {
            double a = start + sweep * i / steps;
            float rr = r + (random.nextFloat() - 0.5f) * amp;
            float x = cx + (float) Math.cos(a) * rr;
            float y = cy + (float) Math.sin(a) * rr;
            if (i == 0) {
                path.moveTo(x, y);
            } else {
                path.lineTo(x, y);
            }
        }
        return path;
    }

    /** Back-and-forth hatching that fills a circle, the way a crayon colours a blob in. */
    static Path scribbleFill(float cx, float cy, float r, float spacing, long seed) {
        Random random = new Random(seed);
        double angle = 0.6 + (random.nextFloat() - 0.5f) * 0.5;
        float cos = (float) Math.cos(angle);
        float sin = (float) Math.sin(angle);
        Path path = new Path();
        boolean first = true;
        boolean flip = false;
        for (float d = -r * 0.9f; d <= r * 0.9f; d += spacing) {
            float half = (float) Math.sqrt(r * r - d * d) * (0.8f + random.nextFloat() * 0.25f);
            float a = flip ? half : -half;
            float b = -a;
            float ax = cx + cos * a - sin * d;
            float ay = cy + sin * a + cos * d;
            float bx = cx + cos * b - sin * d;
            float by = cy + sin * b + cos * d;
            if (first) {
                path.moveTo(ax, ay);
                first = false;
            } else {
                path.lineTo(ax, ay);
            }
            path.lineTo(bx, by);
            flip = !flip;
        }
        return path;
    }

    /** Short strokes radiating from a centre: the spark of a lit fuse. */
    static Path burst(float cx, float cy, float inner, float outerMin, float outerMax, int rays, long seed) {
        Random random = new Random(seed);
        Path path = new Path();
        double offset = random.nextFloat() * Math.PI * 2;
        for (int i = 0; i < rays; i++) {
            double a = offset + Math.PI * 2 * i / rays + (random.nextFloat() - 0.5f) * 0.45f;
            float len = outerMin + (outerMax - outerMin) * random.nextFloat();
            float c = (float) Math.cos(a);
            float s = (float) Math.sin(a);
            path.moveTo(cx + c * inner, cy + s * inner);
            path.lineTo(cx + c * len, cy + s * len);
        }
        return path;
    }
}
