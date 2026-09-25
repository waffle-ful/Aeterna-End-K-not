package dev.allofus.fusioncore.ui;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.DashPathEffect;
import android.graphics.Paint;
import android.graphics.Path;
import android.util.AttributeSet;
import android.view.View;

import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;

import java.util.Random;

import dev.allofus.fusioncore.R;

/**
 * A fuse drawn in crayon across the launch card. {@link #setBurn} moves the spark from the
 * left end (nothing burnt) to the right end (burnt through); the rope ahead of it stays,
 * the rope behind it is left as a smudged trail of ash. While lit the drawing boils, being
 * redrawn slightly differently several times a second.
 */
public class FuseView extends View {
    private static final int BOIL_DRAWINGS = 3;

    private final Paint ropePaint;
    private final Paint ropeEchoPaint;
    private final Paint ashPaint;
    private final Paint sparkPaint;
    private final Paint emberPaint;
    private final Paint corePaint;
    private final Paint[] allPaints;
    private final float density;
    private final Path[] rope = new Path[BOIL_DRAWINGS];
    private final Path[] ropeEcho = new Path[BOIL_DRAWINGS];
    private float burn;
    private boolean lit;
    private long frame;
    private ValueAnimator boilAnimator;

    public FuseView(Context context) {
        this(context, null);
    }

    public FuseView(Context context, @Nullable AttributeSet attrs) {
        super(context, attrs);
        density = getResources().getDisplayMetrics().density;
        int ember = ContextCompat.getColor(context, R.color.ek_ember);
        int afterglow = ContextCompat.getColor(context, R.color.ek_afterglow);

        ropePaint = Crayon.stroke(withAlpha(afterglow, 0.55f), 3.2f * density);
        ropeEchoPaint = Crayon.stroke(withAlpha(afterglow, 0.3f), 1.6f * density);
        ashPaint = Crayon.stroke(ContextCompat.getColor(context, R.color.ek_cinder), 2.2f * density);
        ashPaint.setPathEffect(new DashPathEffect(new float[] {
                1f * density, 4f * density, 2.5f * density, 6f * density, 0.5f * density, 3f * density}, 0));
        sparkPaint = Crayon.stroke(ember, 1.7f * density);
        emberPaint = Crayon.stroke(ember, 2.4f * density);
        corePaint = Crayon.stroke(0xFFFFDEA0, 1.8f * density);
        allPaints = new Paint[] {ropePaint, ropeEchoPaint, ashPaint, sparkPaint, emberPaint, corePaint};
    }

    /** An unlit fuse is drawn at full length with no spark. */
    public void setLit(boolean value) {
        if (lit != value) {
            lit = value;
            updateBoil();
            invalidate();
        }
    }

    /** 0 = just lit at the left end, 1 = burnt through. */
    public void setBurn(float value) {
        burn = Math.max(0f, Math.min(1f, value));
        invalidate();
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        float pad = 6f * density;
        float cy = h / 2f;
        for (int i = 0; i < BOIL_DRAWINGS; i++) {
            rope[i] = Crayon.wobblyLine(pad, w - pad, cy, 2.2f * density, 7f * density, 101 + i);
            ropeEcho[i] = Crayon.wobblyLine(pad + 2f * density, w - pad - 3f * density,
                    cy + 0.9f * density, 2.6f * density, 9f * density, 211 + i);
        }
    }

    @Override
    protected void onVisibilityChanged(View changedView, int visibility) {
        super.onVisibilityChanged(changedView, visibility);
        updateBoil();
    }

    @Override
    protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        updateBoil();
    }

    @Override
    protected void onDetachedFromWindow() {
        stopBoil();
        super.onDetachedFromWindow();
    }

    private void updateBoil() {
        if (lit && isShown() && Motion.enabled(getContext())) {
            if (boilAnimator == null) {
                boilAnimator = ValueAnimator.ofFloat(0f, 1f);
                boilAnimator.setDuration(1000);
                boilAnimator.setRepeatCount(ValueAnimator.INFINITE);
                boilAnimator.addUpdateListener(a -> {
                    long now = Crayon.frameNow();
                    if (now != frame) {
                        frame = now;
                        invalidate();
                    }
                });
                boilAnimator.start();
            }
        } else {
            stopBoil();
        }
    }

    private void stopBoil() {
        if (boilAnimator != null) {
            boilAnimator.cancel();
            boilAnimator = null;
        }
        frame = 0;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        if (rope[0] == null) {
            return;
        }
        int drawing = (int) (frame % BOIL_DRAWINGS);
        for (Paint p : allPaints) {
            Crayon.shiftGrain(p, frame);
        }
        if (!lit) {
            canvas.drawPath(rope[drawing], ropePaint);
            canvas.drawPath(ropeEcho[drawing], ropeEchoPaint);
            return;
        }
        float pad = 6f * density;
        float cy = getHeight() / 2f;
        float x = pad + (getWidth() - 2f * pad) * burn;

        canvas.save();
        canvas.clipRect(0, 0, x, getHeight());
        canvas.drawPath(rope[drawing], ashPaint);
        canvas.restore();

        canvas.save();
        canvas.clipRect(x, 0, getWidth(), getHeight());
        canvas.drawPath(rope[drawing], ropePaint);
        canvas.drawPath(ropeEcho[drawing], ropeEchoPaint);
        canvas.restore();

        long seed = frame * 7919L;
        canvas.drawPath(Crayon.burst(x, cy, 3.5f * density, 7f * density, 13f * density, 9, seed), sparkPaint);
        canvas.drawPath(Crayon.scribbleFill(x, cy, 4f * density, 1.6f * density, seed + 1), emberPaint);
        canvas.drawPath(Crayon.wobblyCircle(x, cy, 1.6f * density, 0.8f * density, seed + 2), corePaint);

        // A couple of loose flecks thrown back over the ash.
        Random random = new Random(seed + 3);
        for (int i = 0; i < 3; i++) {
            float fx = x - (4f + random.nextFloat() * 14f) * density;
            float fy = cy + (random.nextFloat() - 0.5f) * 18f * density;
            float len = (1f + random.nextFloat() * 2f) * density;
            canvas.drawLine(fx, fy, fx - len, fy + len * 0.4f, sparkPaint);
        }
    }

    static int withAlpha(int color, float alpha) {
        return (color & 0x00FFFFFF) | (Math.round(alpha * 255) << 24);
    }
}
