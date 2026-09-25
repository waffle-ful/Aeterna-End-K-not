package dev.allofus.fusioncore.ui;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.util.AttributeSet;
import android.view.View;

import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;

import java.util.Random;

import dev.allofus.fusioncore.R;

/**
 * A single ember drawn in crayon that breathes while the game is being prepared, with a
 * few sparks drifting up off it. The drawing boils like hand-drawn animation.
 */
public class EmberView extends View {
    private static final long BREATH_MS = 2800;
    private static final int SPARKS = 4;
    private static final int SPARK_LIFE_FRAMES = 14;

    private final Paint fillPaint;
    private final Paint outlinePaint;
    private final Paint rayPaint;
    private final Paint corePaint;
    private final Paint sparkPaint;
    private final Paint[] allPaints;
    private final float density;
    private long frame;
    private ValueAnimator animator;

    public EmberView(Context context) {
        this(context, null);
    }

    public EmberView(Context context, @Nullable AttributeSet attrs) {
        super(context, attrs);
        density = getResources().getDisplayMetrics().density;
        int ember = ContextCompat.getColor(context, R.color.ek_ember);
        fillPaint = Crayon.stroke(FuseView.withAlpha(ember, 0.9f), 2.6f * density);
        outlinePaint = Crayon.stroke(ember, 2.2f * density);
        rayPaint = Crayon.stroke(FuseView.withAlpha(ember, 0.75f), 2f * density);
        corePaint = Crayon.stroke(0xFFFFDEA0, 2.4f * density);
        sparkPaint = Crayon.stroke(ContextCompat.getColor(context, R.color.ek_afterglow), 1.8f * density);
        allPaints = new Paint[] {fillPaint, outlinePaint, rayPaint, corePaint, sparkPaint};
    }

    @Override
    protected void onVisibilityChanged(View changedView, int visibility) {
        super.onVisibilityChanged(changedView, visibility);
        update();
    }

    @Override
    protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        update();
    }

    @Override
    protected void onDetachedFromWindow() {
        stop();
        super.onDetachedFromWindow();
    }

    private void update() {
        if (isShown() && Motion.enabled(getContext())) {
            if (animator == null) {
                animator = ValueAnimator.ofFloat(0f, 1f);
                animator.setDuration(1000);
                animator.setRepeatCount(ValueAnimator.INFINITE);
                animator.addUpdateListener(a -> {
                    long now = Crayon.frameNow();
                    if (now != frame) {
                        frame = now;
                        invalidate();
                    }
                });
                animator.start();
            }
        } else {
            stop();
        }
    }

    private void stop() {
        if (animator != null) {
            animator.cancel();
            animator = null;
        }
        frame = 0;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        float cx = getWidth() / 2f;
        float cy = getHeight() / 2f;
        float max = Math.min(cx, cy);
        boolean moving = animator != null;
        // Sampled once per drawing, so the breath steps like the rest of the animation.
        float breath = moving
                ? 0.35f + 0.65f * (0.5f - 0.5f * (float) Math.cos(2 * Math.PI * (frame * Crayon.FRAME_MS % BREATH_MS) / BREATH_MS))
                : 0.6f;
        for (Paint p : allPaints) {
            Crayon.shiftGrain(p, frame);
        }
        long seed = frame * 7919L;

        float bodyR = max * (0.26f + 0.08f * breath);
        canvas.drawPath(Crayon.burst(cx, cy, bodyR + max * 0.12f,
                bodyR + max * (0.22f + 0.1f * breath), bodyR + max * (0.34f + 0.22f * breath), 11, seed), rayPaint);
        canvas.drawPath(Crayon.scribbleFill(cx, cy, bodyR, 2.4f * density, seed + 1), fillPaint);
        canvas.drawPath(Crayon.wobblyCircle(cx, cy, bodyR, 2.4f * density, seed + 2), outlinePaint);
        canvas.drawPath(Crayon.wobblyCircle(cx, cy, max * (0.07f + 0.03f * breath), 1.2f * density, seed + 3), corePaint);

        if (!moving) {
            return;
        }
        for (int i = 0; i < SPARKS; i++) {
            long age = (frame + i * (SPARK_LIFE_FRAMES / SPARKS)) % SPARK_LIFE_FRAMES;
            long born = frame - age;
            Random random = new Random(born * 31L + i);
            float t = age / (float) SPARK_LIFE_FRAMES;
            float sx = cx + (random.nextFloat() - 0.5f) * bodyR * 1.6f + (float) Math.sin(t * 5 + i) * 3f * density;
            float sy = cy - bodyR * 0.6f - t * max * 0.75f;
            float len = (1f - t) * 5f * density + density;
            canvas.drawLine(sx, sy, sx + len * 0.3f, sy - len, sparkPaint);
        }
    }
}
