package dev.allofus.fusioncore.ui;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.DashPathEffect;
import android.graphics.Paint;
import android.graphics.RadialGradient;
import android.graphics.Shader;
import android.util.AttributeSet;
import android.view.View;

import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;

import dev.allofus.fusioncore.R;

/**
 * A fuse drawn across the launch card. {@link #setBurn} moves the ember from the left end
 * (nothing burnt) to the right end (burnt through); the rope ahead of it stays, the rope
 * behind it is left as a dotted trail of ash.
 */
public class FuseView extends View {
    private final Paint ropePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint ashPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint glowPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint corePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final int ember;
    private final int afterglow;
    private final float density;
    private float burn;
    private boolean lit;
    private float flicker = 1f;
    private ValueAnimator flickerAnimator;

    public FuseView(Context context) {
        this(context, null);
    }

    public FuseView(Context context, @Nullable AttributeSet attrs) {
        super(context, attrs);
        density = getResources().getDisplayMetrics().density;
        ember = ContextCompat.getColor(context, R.color.ek_ember);
        afterglow = ContextCompat.getColor(context, R.color.ek_afterglow);

        ropePaint.setStyle(Paint.Style.STROKE);
        ropePaint.setStrokeCap(Paint.Cap.ROUND);
        ropePaint.setStrokeWidth(2.5f * density);
        ropePaint.setColor(withAlpha(afterglow, 0.42f));

        ashPaint.setStyle(Paint.Style.STROKE);
        ashPaint.setStrokeCap(Paint.Cap.ROUND);
        ashPaint.setStrokeWidth(1.5f * density);
        ashPaint.setColor(ContextCompat.getColor(context, R.color.ek_cinder));
        ashPaint.setPathEffect(new DashPathEffect(new float[] {0.5f * density, 5f * density}, 0));

        corePaint.setColor(0xFFFFDEA0);
    }

    /** An unlit fuse is drawn at full length with no ember. */
    public void setLit(boolean value) {
        if (lit != value) {
            lit = value;
            updateFlicker();
            invalidate();
        }
    }

    /** 0 = just lit at the left end, 1 = burnt through. */
    public void setBurn(float value) {
        burn = Math.max(0f, Math.min(1f, value));
        invalidate();
    }

    @Override
    protected void onVisibilityChanged(View changedView, int visibility) {
        super.onVisibilityChanged(changedView, visibility);
        updateFlicker();
    }

    @Override
    protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        updateFlicker();
    }

    @Override
    protected void onDetachedFromWindow() {
        stopFlicker();
        super.onDetachedFromWindow();
    }

    private void updateFlicker() {
        if (lit && isShown() && Motion.enabled(getContext())) {
            if (flickerAnimator == null) {
                flickerAnimator = ValueAnimator.ofFloat(0f, 1f);
                flickerAnimator.setDuration(900);
                flickerAnimator.setRepeatCount(ValueAnimator.INFINITE);
                flickerAnimator.addUpdateListener(a -> {
                    float t = (float) a.getAnimatedValue() * (float) (Math.PI * 2);
                    flicker = 0.9f + 0.06f * (float) Math.sin(t * 3) + 0.04f * (float) Math.sin(t * 7 + 1.3f);
                    invalidate();
                });
                flickerAnimator.start();
            }
        } else {
            stopFlicker();
        }
    }

    private void stopFlicker() {
        if (flickerAnimator != null) {
            flickerAnimator.cancel();
            flickerAnimator = null;
        }
        flicker = 1f;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        float pad = 6f * density;
        float left = pad;
        float right = getWidth() - pad;
        float cy = getHeight() / 2f;
        if (!lit) {
            canvas.drawLine(left, cy, right, cy, ropePaint);
            return;
        }
        float x = left + (right - left) * burn;

        if (x > left) {
            canvas.drawLine(left, cy, x, cy, ashPaint);
        }
        if (x < right) {
            canvas.drawLine(x, cy, right, cy, ropePaint);
        }

        float glowR = 14f * density * flicker;
        glowPaint.setShader(new RadialGradient(x, cy, glowR,
                new int[] {withAlpha(ember, 0.85f), withAlpha(ember, 0.28f), withAlpha(ember, 0f)},
                new float[] {0f, 0.35f, 1f}, Shader.TileMode.CLAMP));
        canvas.drawCircle(x, cy, glowR, glowPaint);
        canvas.drawCircle(x, cy, 2.6f * density * flicker, corePaint);
    }

    static int withAlpha(int color, float alpha) {
        return (color & 0x00FFFFFF) | (Math.round(alpha * 255) << 24);
    }
}
