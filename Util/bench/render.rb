# A software renderer's hot loops, written the way a straightforward Ruby port of a
# 1990s column/span renderer writes them: a 320x240 Integer framebuffer, 64x64 flats,
# column-major wall textures, 34 colormaps of 256 entries, Struct sectors, and column
# loops driven by Range#each with `next`, `[a, b].max`, `#clamp`, `#ceil`, `#to_i` and
# method calls taking ten arguments. It is not any particular engine; it is the shape
# Util/doom (the real thing, fetched) spends its time in, so it can be measured and
# profiled without the game.
#
#   ruby Util/bench/render.rb [kernel] [frames]
#   ./ir.sh Util/bench/render.rb all 40
#
# Kernels: span (floor/ceiling spans), wall (wall columns), segs (the per-seg column
# loop), planes (visplane span assembly), all (a whole frame). Prints one line per
# kernel: its name, ms per frame (best of the timed frames after warmup) and the
# framebuffer checksum, which must be the same on every Ruby.

W = 320
H = 240
HALF_H = H / 2

Sector = Struct.new(:floor_height, :ceiling_height, :light_level, :flat)
Plane = Struct.new(:height, :flat, :light_level, :minx, :maxx, :top, :bottom)
Seg = Struct.new(:x1, :x2, :dist1, :dist2, :sector, :back, :tex, :offset, :length)

class Texture
  attr_reader :width, :height
  def initialize(w, h, seed)
    @width = w; @height = h
    @columns = Array.new(w) do |x|
      Array.new(h) { |y| ((x * 7 + y * 13 + seed) % 251) == 0 ? nil : (x * 31 + y * 17 + seed) % 256 }
    end
  end
  def column_pixels(x) = @columns[x]
end

class Renderer
  attr_reader :framebuffer

  def initialize
    @framebuffer = Array.new(W * H, 0)
    @colormaps = Array.new(34) { |m| Array.new(256) { |c| (c * (34 - m) / 34 + m) & 255 } }
    @flats = Array.new(8) { |f| Array.new(64 * 64) { |i| (i * (f + 3) + (i >> 6) * 11) & 255 } }
    @textures = Array.new(8) { |t| Texture.new(64 + 64 * (t & 1), 128, t * 19) }
    @projection = 160.0
    @player_x = 1056.0; @player_y = -3616.0; @player_z = 41.0
    @column_distscale = Array.new(W) { |x| d = (x - 160) / 160.0; Math.sqrt(1.0 + d * d) }
    @column_cos = Array.new(W) { |x| Math.cos(0.3 + (x - 160) * 0.0045) }
    @column_sin = Array.new(W) { |x| Math.sin(0.3 + (x - 160) * 0.0045) }
    @ceiling_clip = Array.new(W, -1)
    @floor_clip = Array.new(W, H)
    @sectors = Array.new(6) { |s| Sector.new(-8.0 * s, 72.0 + 24 * s, 96 + 24 * s, s & 7) }
    @segs = build_segs
    @planes = build_planes
  end

  def build_segs
    segs = []
    x = 0
    k = 0
    while x < W
      w = 6 + (k * 13) % 29
      x2 = [x + w - 1, W - 1].min
      sector = @sectors[k % 6]
      back = k % 3 == 0 ? @sectors[(k + 1) % 6] : nil
      segs << Seg.new(x, x2, 64.0 + (k * 37) % 400, 64.0 + (k * 53) % 400, sector, back, k & 7, (k * 9) % 64, 64.0 + (k * 11) % 200)
      x = x2 + 1
      k += 1
    end
    segs
  end

  def build_planes
    Array.new(10) do |p|
      minx = (p * 29) % 120
      maxx = [minx + 120 + p * 13, W - 1].min
      top = Array.new(W, H)
      bottom = Array.new(W, -1)
      (minx..maxx).each do |x|
        t = HALF_H + 10 + ((x * (p + 3)) % 40)
        top[x] = t
        bottom[x] = [t + 30 + (x % 50), H - 1].min
      end
      Plane.new(-24.0 + p * 4, p & 7, 100 + p * 12, minx, maxx, top, bottom)
    end
  end

  def reset_clipping
    @ceiling_clip.fill(-1)
    @floor_clip.fill(H)
  end

  def flat_light(level, dist)
    l = 33 - (level >> 3) + (dist / 64.0).to_i
    l.clamp(0, 31)
  end

  def wall_light(level, dist)
    scale = @projection / dist
    l = (level >> 4) * 2 - (scale * 8).to_i
    (31 - l).clamp(0, 31)
  end

  # floor/ceiling span: the per-pixel loop of a textured horizontal span
  def draw_span(plane, y, x1, x2)
    return if x1.nil? || x1 > x2 || y < 0 || y >= H
    dy = y - HALF_H
    return if dy == 0
    plane_height = (plane.height - @player_z).abs
    return if plane_height == 0
    perp_dist = plane_height * @projection / dy.abs
    cmap = @colormaps[flat_light(plane.light_level, perp_dist)]
    framebuffer = @framebuffer
    column_distscale = @column_distscale
    column_cos = @column_cos
    column_sin = @column_sin
    player_x = @player_x
    neg_player_y = -@player_y
    row_offset = y * W
    flat_pixels = @flats[plane.flat]
    x1 = 0 if x1 < 0
    x2 = W - 1 if x2 >= W
    x = x1
    while x <= x2
      ray_dist = perp_dist * column_distscale[x]
      tex_x = (player_x + (ray_dist * column_cos[x])).to_i & 63
      tex_y = (neg_player_y - (ray_dist * column_sin[x])).to_i & 63
      color = flat_pixels[(tex_y * 64) + tex_x]
      framebuffer[row_offset + x] = cmap[color]
      x += 1
    end
  end

  # one wall column: texture column, step per pixel, colormap, nil = transparent
  def draw_wall_column(x, y1, y2, tex, dist, light_level, tex_col, tex_y_start, scale, world_top)
    return if y1 > y2
    clip_top = @ceiling_clip[x] + 1
    clip_bottom = @floor_clip[x] - 1
    y1 = [y1, clip_top].max
    y2 = [y2, clip_bottom].min
    return if y1 > y2
    texture = @textures[tex]
    cmap = @colormaps[wall_light(light_level, dist)]
    framebuffer = @framebuffer
    tex_height = texture.height
    column = texture.column_pixels(tex_col.to_i % texture.width)
    return unless column
    tex_step = 1.0 / scale
    unclipped_y1 = HALF_H - ((world_top - @player_z) * scale)
    tex_y_at_y1 = tex_y_start + ((y1 - unclipped_y1) * tex_step)
    y1 = 0 if y1 < 0
    y2 = H - 1 if y2 >= H
    y = y1
    while y <= y2
      screen_offset = y - y1
      tex_y = (tex_y_at_y1 + (screen_offset * tex_step)).to_i % tex_height
      color = column[tex_y]
      framebuffer[(y * W) + x] = cmap[color] if color
      y += 1
    end
  end

  # the per-seg column loop: interpolate distance, project, clip, draw a column
  def draw_seg(seg)
    x1 = seg.x1; x2 = seg.x2
    sx1 = x1.to_f; sx2 = x2.to_f + 1
    dist1 = seg.dist1; dist2 = seg.dist2
    sector = seg.sector; back = seg.back
    (x1..x2).each do |x|
      next if @ceiling_clip[x] >= @floor_clip[x] - 1
      t = sx2 == sx1 ? 0 : (x - sx1) / (sx2 - sx1)
      t = t.clamp(0.0, 1.0)
      if dist1 > 0 && dist2 > 0
        dist = 1.0 / (((1.0 - t) / dist1) + (t / dist2))
      else
        dist = dist1 > 0 ? dist1 : dist2
      end
      tex_col = (seg.offset + (t * seg.length)).to_i
      next if dist < 1
      scale = @projection / dist
      front_floor = sector.floor_height - @player_z
      front_ceil = sector.ceiling_height - @player_z
      ceil_y = (HALF_H - (front_ceil * scale)).ceil
      floor_y = (HALF_H - (front_floor * scale)).to_i
      ceil_y = [ceil_y, @ceiling_clip[x] + 1].max
      floor_y = [floor_y, @floor_clip[x] - 1].min
      if back
        back_ceil_y = (HALF_H - ((back.ceiling_height - @player_z) * scale)).ceil
        back_floor_y = (HALF_H - ((back.floor_height - @player_z) * scale)).to_i
        if back_ceil_y > ceil_y
          draw_wall_column(x, ceil_y, [back_ceil_y - 1, floor_y].min, seg.tex, dist, sector.light_level,
                           tex_col, 0.0, scale, sector.ceiling_height)
          @ceiling_clip[x] = [back_ceil_y - 1, floor_y].min
        else
          @ceiling_clip[x] = ceil_y - 1
        end
        if back_floor_y < floor_y
          draw_wall_column(x, [back_floor_y + 1, ceil_y].max, floor_y, (seg.tex + 1) & 7, dist,
                           sector.light_level, tex_col, 0.0, scale, back.floor_height)
          @floor_clip[x] = [back_floor_y + 1, ceil_y].max
        else
          @floor_clip[x] = floor_y + 1
        end
      else
        draw_wall_column(x, ceil_y, floor_y, seg.tex, dist, sector.light_level, tex_col, 0.0, scale,
                         sector.ceiling_height)
        @ceiling_clip[x] = H
        @floor_clip[x] = -1
      end
    end
  end

  # visplane -> spans: open and close a span per row as the columns go by
  def render_plane(plane)
    return if plane.minx > plane.maxx
    spanstart = Array.new(H)
    ((plane.minx)..(plane.maxx + 1)).each do |x|
      if x <= plane.maxx
        t2 = plane.top[x]
        b2 = plane.bottom[x]
        t2 = H if t2 > b2
      else
        t2 = H
        b2 = -1
      end
      if x > plane.minx
        t1 = plane.top[x - 1]
        b1 = plane.bottom[x - 1]
        t1 = H if t1 > b1
      else
        t1 = H
        b1 = -1
      end
      if t1 < H
        (t1..[b1, t2 - 1].min).each do |y|
          draw_span(plane, y, spanstart[y], x - 1) if spanstart[y]
          spanstart[y] = nil
        end
        ([t1, b2 + 1].max..b1).each do |y|
          draw_span(plane, y, spanstart[y], x - 1) if spanstart[y]
          spanstart[y] = nil
        end
      end
      next unless t2 < H
      (t2..b2).each do |y|
        spanstart[y] ||= x
      end
    end
  end

  def kernel_span
    plane = @planes[0]
    y = HALF_H + 1
    while y < H
      draw_span(plane, y, 0, W - 1)
      y += 1
    end
  end

  def kernel_wall
    x = 0
    while x < W
      draw_wall_column(x, 20 + (x % 30), 200 - (x % 40), x & 7, 80.0 + x, 160, x * 3, 0.0, 1.5 + (x % 7) * 0.25, 64.0)
      x += 1
    end
  end

  def kernel_segs
    reset_clipping
    @segs.each { |s| draw_seg(s) }
  end

  def kernel_planes
    @planes.each { |p| render_plane(p) }
  end

  def frame(kernel)
    case kernel
    when "span" then kernel_span
    when "wall" then kernel_wall
    when "segs" then kernel_segs
    when "planes" then kernel_planes
    else
      reset_clipping
      @segs.each { |s| draw_seg(s) }
      @planes.each { |p| render_plane(p) }
    end
  end

  def checksum
    ck = 0
    @framebuffer.each_with_index { |v, i| ck = (ck * 31 + v * (i & 15)) & 0xFFFFFFF }
    ck
  end
end

kernels = ARGV[0].nil? || ARGV[0] == "each" ? %w[span wall segs planes all] : [ARGV[0]]
frames = Integer(ARGV[1] || 30)
warmup = Integer(ENV["WARMUP"] || 20)
kernels.each do |k|
  r = Renderer.new
  warmup.times { r.frame(k) }
  best = nil
  frames.times do
    t0 = Process.clock_gettime(Process::CLOCK_MONOTONIC)
    r.frame(k)
    ms = (Process.clock_gettime(Process::CLOCK_MONOTONIC) - t0) * 1000.0
    best = ms if best.nil? || ms < best
  end
  printf("%-7s %8.3f ms  checksum %d\n", k, best, r.checksum)
end
